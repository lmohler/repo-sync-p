using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// The SVN round trip (ported from the earlier git-sync-v2 tool) for a working copy
/// whose git 'main' mirrors SVN, with a work branch checked out in its own worktree folder:
///   SEND   - applies the branch's changes (main..work) to the SVN working copy files, runs svn add/delete,
///            and opens the TortoiseSVN commit dialog. It never runs 'svn commit';
///            once the dialog closes with the working copy clean (committed), it runs SYNC.
///   SYNC   - 'svn update', records it on main, merges main back into the branch.
///   UNDO   - reverts what the last Send applied, before it was committed.
///   SHIP   - checks the branch is committed, then SEND, the SVN commit (TortoiseSVN, or 'svn commit -m' when given
///            a message) and SYNC in one go.
/// Besides the log lines, Send / Sync / Ship report each phase to <see cref="Steps"/> (pending / running / done / failed).
/// Every step stops at the first problem with a <see cref="UserFacingException"/>; the step that stopped changed nothing.
/// </summary>
public sealed class SvnWorkflow
{
    private static readonly HashSet<string> MirrorBranches = new(StringComparer.Ordinal) { "main", "master" };
    private const int SvnPathsPerCall = 50;

    private const string DefaultFooter =
        "Your SVN working copy files were not changed by the step that stopped. Steps above marked OK did run (svn update and recording SVN on main are always safe).";

    private readonly string _wc;
    private readonly string _branch;
    private readonly IProgress<LogEntry> _log;
    private readonly CancellationToken _ct;
    private string _mirrorBranch = "main";
    private string _footer = DefaultFooter;

    public SvnWorkflow(string svnWorkingCopy, string branch, IProgress<LogEntry> log, CancellationToken ct = default)
    {
        _wc = Path.GetFullPath(svnWorkingCopy).TrimEnd('\\', '/');
        _branch = branch;
        _log = log;
        _ct = ct;
    }

    /// <summary>Structured progress for the SVN tab's step panel, alongside the log lines. Optional.</summary>
    public IProgress<SvnStepProgress>? Steps { get; set; }

    /// <summary>The last Send, for Undo. Kept inside .git so neither SVN nor git sees it. Same file v2 used.</summary>
    private string StateFile => Path.Combine(_wc, ".git", "git-sync-last-send.json");

    public bool HasPendingSend => File.Exists(StateFile);

    // ---------------------------------------------------------------------------------------------
    // Output / running
    // ---------------------------------------------------------------------------------------------

    private void Step(string text) => _log.Report(new LogEntry(text, LogLevel.Header));
    private void Ok(string text) => _log.Report(new LogEntry($"    OK  {text}", LogLevel.Success));
    private void Info(string text) => _log.Report(new LogEntry($"        {text}"));
    private void Warn(string text) => _log.Report(new LogEntry($"    !!  {text}", LogLevel.Warning));
    private void DryRun(string text) => _log.Report(new LogEntry($"        (preview) {text}"));
    private void Done(string text) => _log.Report(new LogEntry(text, LogLevel.Success));

    // ---------------------------------------------------------------------------------------------
    // Step progress: the first public call plans its steps (nested calls, e.g. Send -> Sync, reuse the plan),
    // each phase Begins / Completes one, and Tracked marks the running one Failed when something throws.
    // ---------------------------------------------------------------------------------------------

    public const string StepCheck = "check", StepPrepare = "prepare", StepApply = "apply", StepCommit = "commit",
        StepUpdate = "update", StepRecord = "record", StepMerge = "merge";

    private readonly List<(string Id, string Title)> _plan = new();
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private string? _currentStep;

    private static readonly (string, string)[] SyncPlan =
    {
        (StepUpdate, "svn update"), (StepRecord, "Record SVN on git main"), (StepMerge, "Merge main into the work branch"),
    };

    private static readonly (string, string)[] SendPlan = new[]
    {
        (StepPrepare, "Bring SVN and the branch up to date"), (StepApply, "Apply the branch to the SVN working copy"),
        (StepCommit, "Commit to SVN"),
    }.Concat(SyncPlan).ToArray();

    private void Plan(IEnumerable<(string Id, string Title)> steps)
    {
        if (_plan.Count > 0) return;
        _plan.AddRange(steps);
        foreach (var (id, title) in _plan) Steps?.Report(new SvnStepProgress(id, title, SvnStepState.Pending));
    }

    private void ReportStep(string id, SvnStepState state, string? message)
    {
        var title = _plan.FirstOrDefault(p => p.Id == id).Title;
        if (title is not null) Steps?.Report(new SvnStepProgress(id, title, state, message));
    }

    private void Begin(string id)
    {
        _currentStep = id;
        _started.Add(id);
        ReportStep(id, SvnStepState.Running, null);
    }

    private void Complete(string? message = null)
    {
        if (_currentStep is { } id) ReportStep(id, SvnStepState.Done, message);
        _currentStep = null;
    }

    /// <summary>Marks the steps that never started as skipped (nothing to send, preview, not committed).</summary>
    private void SkipRest(string message)
    {
        foreach (var (id, _) in _plan.Where(p => !_started.Contains(p.Id)).ToList())
        {
            _started.Add(id);
            ReportStep(id, SvnStepState.Skipped, message);
        }
    }

    private void FailCurrent(string message)
    {
        if (_currentStep is { } id) ReportStep(id, SvnStepState.Failed, message);
        _currentStep = null;
        SkipRest("Not run");
    }

    private async Task<string> Tracked(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            FailCurrent("Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            FailCurrent(ex.Message);
            throw;
        }
    }

    private UserFacingException Stop(string message, IEnumerable<string>? help = null) =>
        new(message, (help ?? Array.Empty<string>()).Append(_footer).ToArray());

    private async Task<GitResult> GitAsync(IReadOnlyList<string> args, string? workDir = null, bool allowFail = false)
    {
        var result = await Git.RunAsync(workDir ?? _wc, args, _ct);
        if (!result.Success && !allowFail)
            throw Stop($"'git {string.Join(' ', args)}' failed (exit {result.ExitCode}).", new[] { result.CombinedOutput });
        return result;
    }

    private async Task<GitResult> SvnAsync(IReadOnlyList<string> args, TimeSpan? timeout = null, bool allowFail = false)
    {
        var result = await Svn.RunAsync(_wc, args, timeout, _ct);
        if (!result.Success && !allowFail)
        {
            throw Stop($"'svn {string.Join(' ', args)}' failed (exit {result.ExitCode}).", new[]
            {
                result.CombinedOutput,
                "If SVN needs your password, run any svn command against the repo once in a terminal (e.g. 'svn update') so the login is saved - the app runs svn non-interactively.",
            });
        }
        return result;
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private async Task AssertToolsAsync()
    {
        if (!(await Git.RunAsync(Path.GetTempPath(), new[] { "--version" }, _ct)).Success)
            throw Stop("'git' is not on PATH.", new[] { "Install Git for Windows (git-scm.com), then run this again." });
        if (!(await Svn.RunAsync(Path.GetTempPath(), new[] { "--version", "--quiet" }, ct: _ct)).Success)
            throw Stop("'svn' is not on PATH.", new[] { "Install the SVN command-line tools (the TortoiseSVN installer has a 'command line client tools' option)." });
    }

    // ---------------------------------------------------------------------------------------------
    // SVN helpers
    // ---------------------------------------------------------------------------------------------

    private sealed record SvnChange(string Item, string Path, bool TreeConflict);

    /// <summary>Local SVN changes. Unversioned paths git also ignores, and git's own files, are left out.</summary>
    private async Task<List<SvnChange>> GetSvnChangesAsync()
    {
        var result = await SvnAsync(new[] { "status", "--xml", "--ignore-externals" });
        var changes = new List<SvnChange>();
        foreach (var entry in XDocument.Parse(result.StdOut).Descendants("entry"))
        {
            var path = (entry.Attribute("path")?.Value ?? string.Empty).Replace('\\', '/');
            var wcStatus = entry.Element("wc-status");
            var item = wcStatus?.Attribute("item")?.Value ?? "none";
            var treeConflict = wcStatus?.Attribute("tree-conflicted")?.Value == "true";

            if ((path is "" or "." || item is "normal" or "external" or "none") && !treeConflict) continue;
            if (Svn.GitOnlyFiles.Contains(path)) continue;
            if (item == "unversioned" && await Svn.IsGitIgnoredAsync(_wc, path, _ct)) continue;
            changes.Add(new SvnChange(item, path, treeConflict));
        }
        return changes;
    }

    private static string Format(SvnChange change)
    {
        var code = change.Item switch
        {
            "added" => "A", "deleted" => "D", "modified" => "M", "unversioned" => "?", "missing" => "!",
            "conflicted" => "C", "replaced" => "R", _ => change.Item,
        };
        if (change.TreeConflict) code += " (tree conflict)";
        return $"{code}  {change.Path}";
    }

    private async Task<string> GetSvnRevisionAsync() => (await SvnAsync(new[] { "info", "--show-item", "revision" })).StdOut.Trim();

    /// <summary>svn reads an '@' in a path as a peg revision; a trailing '@' makes it literal.</summary>
    private static string ToSvnTarget(string path) => path.Contains('@') ? path + "@" : path;

    /// <summary>Drops paths inside another path in the list (svn already acts on a folder's contents).</summary>
    private static List<string> TopMostPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths.Distinct().OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!result.Any(r => path.StartsWith(r + "/", StringComparison.Ordinal))) result.Add(path);
        }
        return result;
    }

    /// <summary>Literal git pathspecs in batches that keep every command line under Windows' limit. No paths
    /// gives one empty batch: the whole branch.</summary>
    private static List<List<string>> PathBatches(IReadOnlyList<string> paths)
    {
        const int MaxBatchChars = 20_000;
        var batches = new List<List<string>>();
        if (paths.Count == 0)
        {
            batches.Add(new List<string>());
            return batches;
        }
        List<string>? current = null;
        var length = 0;
        foreach (var path in paths)
        {
            var spec = ":(literal)" + path;
            if (current is null || length + spec.Length + 3 > MaxBatchChars)
            {
                current = new List<string> { "--" };
                batches.Add(current);
                length = 0;
            }
            current.Add(spec);
            length += spec.Length + 3;
        }
        return batches;
    }

    private async Task<int> SvnOnPathsAsync(IReadOnlyList<string> svnArgs, IEnumerable<string> paths)
    {
        var targets = TopMostPaths(paths).Select(ToSvnTarget).ToList();
        foreach (var chunk in targets.Chunk(SvnPathsPerCall)) await SvnAsync(svnArgs.Concat(new[] { "--" }).Concat(chunk).ToList());
        return targets.Count;
    }

    // ---------------------------------------------------------------------------------------------
    // Shared steps
    // ---------------------------------------------------------------------------------------------

    /// <summary>Checks the layout Send/Sync/Undo rely on and returns the branch's worktree folder.</summary>
    private async Task<string> AssertSetupAsync()
    {
        await AssertToolsAsync();
        if (!Svn.IsWorkingCopy(_wc)) throw Stop($"{_wc} is not an SVN working copy.");
        if (!Directory.Exists(Path.Combine(_wc, ".git"))) throw Stop($"{_wc} is not a git repo.");

        var current = (await GitAsync(new[] { "branch", "--show-current" })).StdOut.Trim();
        if (!MirrorBranches.Contains(current))
            throw Stop($"git in {_wc} is on '{current}', not 'main'.", new[] { $"Switch it back first:  git -C \"{_wc}\" switch main" });
        _mirrorBranch = current;

        var autocrlf = (await GitAsync(new[] { "config", "core.autocrlf" }, allowFail: true)).StdOut.Trim();
        if (autocrlf != "false")
        {
            throw Stop($"core.autocrlf is '{autocrlf}' in this repo; it must be 'false' so files reach SVN byte for byte.",
                new[] { $"Fix:  git -C \"{_wc}\" config core.autocrlf false" });
        }

        var worktree = (await Git.GetWorktreesAsync(_wc, _ct)).FirstOrDefault(w => !w.IsMain && w.Branch == _branch && !w.Prunable)?.Path;
        if (worktree is null)
        {
            throw Stop($"Branch '{_branch}' is not checked out in any worktree folder.",
                new[] { $"Create one on the Worktrees tab, or:  git -C \"{_wc}\" worktree add \"{_wc}-{_branch}\" {_branch}" });
        }
        return worktree;
    }

    private async Task AssertSvnCleanAsync(string why)
    {
        var changes = await GetSvnChangesAsync();
        if (changes.Count > 0)
        {
            var help = changes.Take(25).Select(Format).ToList();
            if (changes.Count > 25) help.Add($"... and {changes.Count - 25} more");
            help.Add("If they are from the last Send and you no longer want them: SVN tab > Undo last send. Otherwise commit or revert them in SVN first.");
            throw Stop($"The SVN working copy has {changes.Count} local change(s). {why}", help);
        }
        Ok("SVN working copy has no local changes.");
    }

    private async Task UpdateSvnAsync(bool dryRun)
    {
        if (dryRun)
        {
            var status = await SvnAsync(new[] { "status", "--show-updates", "--xml", "--ignore-externals", "--non-interactive" }, Svn.UpdateTimeout);
            var incoming = XDocument.Parse(status.StdOut).Descendants("entry")
                .Count(e => e.Element("repos-status")?.Attribute("item")?.Value is { } item && item != "none");
            if (incoming > 0) DryRun($"'svn update' would bring in {incoming} changed path(s).");
            else Ok($"(preview) Already at the latest SVN revision (r{await GetSvnRevisionAsync()}).");
            return;
        }

        var before = await GetSvnRevisionAsync();
        await SvnAsync(new[] { "update", "--accept", "postpone", "--non-interactive" }, Svn.UpdateTimeout);
        var after = await GetSvnRevisionAsync();
        Ok(before == after ? $"Already at the latest SVN revision (r{after})." : $"Updated SVN r{before} -> r{after}.");

        var conflicts = (await GetSvnChangesAsync()).Where(c => c.Item == "conflicted" || c.TreeConflict).ToList();
        if (conflicts.Count > 0)
            throw Stop("'svn update' left conflicts - resolve them in SVN (TortoiseSVN > Resolve), then run this again.", conflicts.Select(Format));
    }

    /// <summary>Records SVN's committed state on git 'main' (mirror commit + svn-rNNNN tag).</summary>
    private async Task SyncMainWithSvnAsync(bool dryRun)
    {
        var result = await Svn.MirrorCommitAsync(_wc, dryRun, _ct);
        if (!result.Success) throw Stop($"Could not record SVN on git '{_mirrorBranch}'.", new[] { result.Error ?? string.Empty });

        if (result.WouldCommit) DryRun($"Would record on git '{_mirrorBranch}': {result.Message}");
        else if (result.Committed) Ok($"Recorded on git '{_mirrorBranch}': {result.Message}");
        else Ok($"git '{_mirrorBranch}' matches SVN {result.Revision}.");
        if (result.Warning is not null) Warn(result.Warning);
    }

    private async Task AssertWorktreeCleanAsync(string worktree)
    {
        var status = (await GitAsync(new[] { "status", "--porcelain" }, worktree)).StdOut;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var help = Lines(status).Take(25).ToList();
            help.Add($"Commit them on '{_branch}' first (select the {Path.GetFileName(worktree)} card > Changes tab).");
            throw Stop($"Your branch '{_branch}' has uncommitted changes in {worktree}.", help);
        }
        Ok($"Branch '{_branch}' has no uncommitted changes.");
    }

    /// <summary>Brings the latest SVN ('main') into the branch, so its diff is only your work. A conflict undoes the merge.</summary>
    private async Task MergeMainIntoBranchAsync(string worktree, bool dryRun)
    {
        if ((await GitAsync(new[] { "merge-base", "--is-ancestor", _mirrorBranch, _branch }, allowFail: true)).Success)
        {
            Ok($"Branch '{_branch}' already contains the latest '{_mirrorBranch}'.");
            return;
        }
        if (dryRun)
        {
            DryRun($"Would merge '{_mirrorBranch}' into '{_branch}'.");
            return;
        }

        var merge = await GitAsync(new[] { "merge", "--no-edit", _mirrorBranch }, worktree, allowFail: true);
        if (!merge.Success)
        {
            var conflicts = Lines((await GitAsync(new[] { "diff", "--name-only", "--diff-filter=U" }, worktree, allowFail: true)).StdOut);
            await GitAsync(new[] { "merge", "--abort" }, worktree, allowFail: true);
            var help = conflicts.Select(c => $"conflict: {c}").ToList();
            help.Add($"Resolve it in {worktree}: git merge {_mirrorBranch}, fix the files (Changes tab), commit, then run this again.");
            throw Stop("Your branch conflicts with the latest SVN changes. The merge was undone.", help);
        }
        Ok($"Merged the latest '{_mirrorBranch}' into '{_branch}'.");
    }

    // ---------------------------------------------------------------------------------------------
    // SEND
    // ---------------------------------------------------------------------------------------------

    /// <summary>Sends the branch's changes (all, or only <paramref name="paths"/>) to the SVN working copy. Returns a short outcome.
    /// With <paramref name="commitMessage"/> it runs 'svn commit -m' itself instead of opening the TortoiseSVN dialog.</summary>
    public Task<string> SendAsync(IReadOnlyList<string> paths, bool dryRun, bool openCommitDialog = true, string? commitMessage = null)
    {
        Plan(SendPlan);
        return Tracked(() => SendCoreAsync(paths, dryRun, openCommitDialog, commitMessage));
    }

    /// <summary>The whole trip in one go: the branch must be committed, then Send, the SVN commit (TortoiseSVN, or
    /// 'svn commit -m' with <paramref name="commitMessage"/>), and Sync.</summary>
    public Task<string> ShipAsync(string? commitMessage)
    {
        Plan(new[] { (StepCheck, "Work branch is committed") }.Concat(SendPlan));
        return Tracked(async () =>
        {
            Step("Ship to SVN");
            Begin(StepCheck);
            var worktree = await AssertSetupAsync();
            await AssertWorktreeCleanAsync(worktree);
            Complete($"'{_branch}' has no uncommitted changes");
            return await SendCoreAsync(Array.Empty<string>(), dryRun: false, openCommitDialog: true, commitMessage);
        });
    }

    private async Task<string> SendCoreAsync(IReadOnlyList<string> paths, bool dryRun, bool openCommitDialog, string? commitMessage)
    {
        Begin(StepPrepare);
        var worktree = await AssertSetupAsync();
        var pathArgs = paths.Select(p => p.Trim().Replace('\\', '/').Trim('/')).Where(p => p.Length > 0).ToList();
        Info($"SVN working copy: {_wc} (git '{_mirrorBranch}')");
        Info($"Branch          : {_branch} ({worktree})");
        if (pathArgs.Count is > 0 and <= 10) Info($"Only these paths: {string.Join(", ", pathArgs)}");
        else if (pathArgs.Count > 10) Info($"Only these {pathArgs.Count} paths: {string.Join(", ", pathArgs.Take(10))}, ...");

        Step("[1/7] SVN working copy");
        await AssertSvnCleanAsync("Send needs a clean working copy so only your branch changes end up in it.");

        Step("[2/7] svn update");
        await UpdateSvnAsync(dryRun);

        Step($"[3/7] Make git '{_mirrorBranch}' match SVN");
        await SyncMainWithSvnAsync(dryRun);

        Step($"[4/7] Your branch '{_branch}' ({worktree})");
        await AssertWorktreeCleanAsync(worktree);
        await MergeMainIntoBranchAsync(worktree, dryRun);
        Complete(dryRun ? "Checked (preview)" : $"SVN r{await GetSvnRevisionAsync()}; '{_branch}' up to date with it");

        Begin(StepApply);
        Step("[5/7] Changes to send");
        var diffBase = new List<string> { "diff", "--no-renames" };
        var batches = PathBatches(pathArgs);
        var names = new List<string>();
        foreach (var batch in batches)
            names.AddRange(Lines((await GitAsync(diffBase.Concat(new[] { "--name-status", _mirrorBranch, _branch }).Concat(batch).ToList())).StdOut));
        if (names.Count == 0)
        {
            Ok($"Nothing to send: '{_branch}' has no changes compared with SVN.");
            Complete("Nothing to send");
            SkipRest("Nothing to send");
            return "Nothing to send";
        }

        var added = names.Count(n => n.StartsWith('A'));
        var deleted = names.Count(n => n.StartsWith('D'));
        Info($"{names.Count} file(s): {names.Count - added - deleted} modified, {added} new, {deleted} deleted");
        foreach (var line in names.Take(40)) Info(line.Replace('\t', ' ').Insert(1, " "));
        if (names.Count > 40) Info($"... and {names.Count - 40} more");

        if (dryRun)
        {
            Done("Preview: nothing was changed. Press Send to send.");
            Complete($"Would send {names.Count} file(s)");
            SkipRest("Preview");
            return $"Would send {names.Count} file(s)";
        }

        // one patch per batch; every patch is checked before any is applied
        var patches = batches.Select(_ => Path.Combine(Path.GetTempPath(), $"repo-sync-send-to-svn-{Guid.NewGuid():N}.patch")).ToList();
        try
        {
            for (var i = 0; i < batches.Count; i++)
                await GitAsync(diffBase.Concat(new[] { "--binary", $"--output={patches[i]}", _mirrorBranch, _branch }).Concat(batches[i]).ToList());
            var nonEmpty = patches.Where(p => File.Exists(p) && new FileInfo(p).Length > 0).ToList();
            foreach (var patch in nonEmpty)
            {
                var check = await GitAsync(new[] { "apply", "--check", "--whitespace=nowarn", patch }, allowFail: true);
                if (!check.Success) throw Stop("The changes do not apply cleanly to the SVN working copy.", new[] { check.StdErr.Trim() });
            }
            foreach (var patch in nonEmpty) await GitAsync(new[] { "apply", "--whitespace=nowarn", patch });
        }
        finally
        {
            foreach (var patch in patches)
            {
                try { File.Delete(patch); } catch { /* best effort */ }
            }
        }
        Ok("Applied to the SVN working copy files.");
        _footer = "The branch's changes are now in the SVN working copy files. To take them back: SVN tab > Undo last send.";

        Step("[6/7] Tell SVN about new and deleted files");
        var changes = await GetSvnChangesAsync();
        var toAdd = changes.Where(c => c.Item == "unversioned").Select(c => c.Path).ToList();
        var toDelete = changes.Where(c => c.Item == "missing").Select(c => c.Path).ToList();
        var addedCount = toAdd.Count > 0 ? await SvnOnPathsAsync(new[] { "add", "--parents" }, toAdd) : 0;
        var deletedCount = toDelete.Count > 0 ? await SvnOnPathsAsync(new[] { "delete" }, toDelete) : 0;
        Ok($"svn add: {addedCount} path(s), svn delete: {deletedCount} path(s).");

        var final = await GetSvnChangesAsync();
        var state = new SendState
        {
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Branch = _branch,
            Revision = await GetSvnRevisionAsync(),
            Changes = final.Select(c => new SendStateChange { Item = c.Item, Path = c.Path }).ToList(),
        };
        await File.WriteAllTextAsync(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), _ct);

        foreach (var problem in final.Where(c => c.Item is "unversioned" or "missing" or "conflicted" || c.TreeConflict))
            Warn($"Needs a look: {Format(problem)}");

        Complete($"{final.Count} path(s) in the SVN working copy");

        Begin(StepCommit);
        Step("[7/7] Ready for you to commit to SVN");
        foreach (var change in final.Take(40)) Info(Format(change));
        if (final.Count > 40) Info($"... and {final.Count - 40} more");

        if (!string.IsNullOrWhiteSpace(commitMessage))
        {
            // inline message from the SVN tab: commit straight away, no dialog
            var commit = await SvnAsync(new[] { "commit", "--non-interactive", "-m", commitMessage.Trim() }, Svn.UpdateTimeout);
            var committed = Lines(commit.StdOut).LastOrDefault(l => l.StartsWith("Committed revision", StringComparison.Ordinal))?.TrimEnd('.');
            Ok(committed ?? "svn commit done.");
        }
        else
        {
            var tortoise = openCommitDialog ? Svn.FindTortoiseTool("TortoiseProc.exe") : null;
            if (tortoise is null)
            {
                Done($"Review with 'svn diff', then commit:  svn commit -m \"...\"   (in {_wc})");
                Done("After the SVN commit: SVN tab > Sync from SVN.   Changed your mind? SVN tab > Undo last send.");
                Complete("Waiting for you to commit in SVN");
                SkipRest("Run Sync from SVN after the SVN commit");
                return $"Sent {final.Count} path(s) - commit in SVN, then Sync from SVN";
            }

            using (var dialog = Process.Start(new ProcessStartInfo(tortoise, $"/command:commit /path:\"{_wc}\"") { UseShellExecute = false }))
            {
                Done("The TortoiseSVN commit dialog is open: review the files, type the message (e.g. ARC30-1234 ...), press OK.");
                if (dialog is not null) await dialog.WaitForExitAsync(_ct);
            }
        }

        // a clean working copy means the commit went through: sync right away
        var left = await GetSvnChangesAsync();
        if (left.Count > 0)
        {
            Warn($"SVN still shows {left.Count} local change(s) - the commit was cancelled or only partly done.");
            Done("After the SVN commit: SVN tab > Sync from SVN.   Changed your mind? SVN tab > Undo last send.");
            FailCurrent($"Not committed: SVN still shows {left.Count} local change(s). Commit, then Sync from SVN (or Undo last send).");
            return $"Sent {final.Count} path(s) - not committed yet";
        }
        Ok("SVN commit done.");
        Complete("Committed");
        _footer = DefaultFooter;
        return await SyncFromSvnAsync(dryRun: false);
    }

    private sealed class SendState
    {
        public string Created { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public List<SendStateChange> Changes { get; set; } = new();
    }

    private sealed class SendStateChange
    {
        public string Item { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // SYNC FROM SVN
    // ---------------------------------------------------------------------------------------------

    public Task<string> SyncFromSvnAsync(bool dryRun)
    {
        Plan(SyncPlan);
        return Tracked(() => SyncCoreAsync(dryRun));
    }

    private async Task<string> SyncCoreAsync(bool dryRun)
    {
        Begin(StepUpdate);
        var worktree = await AssertSetupAsync();
        Info($"SVN working copy: {_wc} (git '{_mirrorBranch}')");
        Info($"Branch          : {_branch} ({worktree})");

        Step("[1/4] SVN working copy");
        await AssertSvnCleanAsync("Commit (or Undo) the sent changes before running Sync from SVN.");

        Step("[2/4] svn update");
        await UpdateSvnAsync(dryRun);
        Complete(dryRun ? "Checked (preview)" : $"At SVN r{await GetSvnRevisionAsync()}");

        Begin(StepRecord);
        Step($"[3/4] Record SVN on git '{_mirrorBranch}'");
        await SyncMainWithSvnAsync(dryRun);
        Complete(dryRun ? "Checked (preview)" : $"git '{_mirrorBranch}' matches SVN");

        Begin(StepMerge);
        Step($"[4/4] Bring '{_mirrorBranch}' back into '{_branch}'");
        await AssertWorktreeCleanAsync(worktree);
        await MergeMainIntoBranchAsync(worktree, dryRun);

        if (dryRun)
        {
            Done("Preview: nothing was changed. Press Sync from SVN to sync.");
            Complete("Checked (preview)");
            return "Sync preview done";
        }
        try { File.Delete(StateFile); } catch { /* best effort */ }
        var revision = await GetSvnRevisionAsync();
        Done($"Done. git '{_mirrorBranch}' matches SVN r{revision}, and '{_branch}' is up to date with it.");
        Complete($"'{_branch}' up to date with SVN r{revision}");
        return $"Matches SVN r{revision}; '{_branch}' up to date";
    }

    /// <summary>Only records SVN's committed state on 'main' (no svn update, no merge).</summary>
    public async Task<string> RecordMirrorAsync()
    {
        await AssertSetupAsync();
        Step($"Record SVN on git '{_mirrorBranch}'");
        await SyncMainWithSvnAsync(dryRun: false);
        return "Recorded";
    }

    // ---------------------------------------------------------------------------------------------
    // UNDO
    // ---------------------------------------------------------------------------------------------

    public async Task<string> UndoAsync()
    {
        await AssertSetupAsync();
        Step("UNDO - revert what the last Send applied to the SVN working copy");
        _footer = "Undo only touches paths SVN still shows as changed, so it is safe to run it again.";
        if (!File.Exists(StateFile)) throw Stop("There is no previous Send to undo.");

        var state = JsonSerializer.Deserialize<SendState>(await File.ReadAllTextAsync(StateFile, Encoding.UTF8, _ct)) ?? new SendState();

        // only paths SVN still reports as locally changed: anything already committed is left alone
        var pending = (await GetSvnChangesAsync()).ToDictionary(c => c.Path, c => c.Item);
        var targets = state.Changes.Where(c => pending.ContainsKey(c.Path)).ToList();
        if (targets.Count == 0)
        {
            File.Delete(StateFile);
            Ok("Nothing left to undo (already committed or reverted).");
            return "Nothing to undo";
        }

        var revert = targets.Where(c => pending[c.Path] != "unversioned").Select(c => c.Path).ToList();
        if (revert.Count > 0) await SvnOnPathsAsync(new[] { "revert", "--depth", "infinity" }, revert);

        // files the Send created are now unversioned: remove them (only paths SVN reports as unversioned right now)
        var createdBySend = state.Changes.Where(c => c.Item is "added" or "unversioned").Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var now in (await GetSvnChangesAsync()).Where(c => c.Item == "unversioned" && createdBySend.Contains(c.Path)))
        {
            var full = Path.Combine(_wc, now.Path.Replace('/', '\\'));
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
        }

        File.Delete(StateFile);
        Ok($"Reverted {targets.Count} path(s). The SVN working copy is back to how it was before the Send.");
        var left = await GetSvnChangesAsync();
        if (left.Count > 0) Warn($"SVN still shows {left.Count} local change(s) that were not part of the Send.");
        return "Send undone";
    }

    // ---------------------------------------------------------------------------------------------
    // SETUP (Add SVN repo)
    // ---------------------------------------------------------------------------------------------

    private const string SetupFooter = "Steps above marked OK did run and are kept; running Add SVN repo again skips them and carries on.";

    /// <summary>
    /// Builds the layout for an SVN repo. Each step that is already done is skipped, so it is safe to run on a
    /// working copy that is already (partly) set up:
    ///   1. check out the SVN URL into the folder, unless the folder already is a working copy,
    ///   2. 'git init -b main' there, with core.autocrlf=false so files reach SVN byte for byte,
    ///   3. a .gitignore that excludes .svn plus the folder's svn:ignore / svn:global-ignores rules,
    ///   4. the first "SVN rNNNN mirror" commit on main (tagged svn-rNNNN),
    ///   5. the work branch, checked out in its own worktree folder next to the working copy.
    /// Returns the worktree folder.
    /// </summary>
    public async Task<string> SetupAsync(string? svnUrl, string worktreePath)
    {
        _footer = SetupFooter;
        worktreePath = Path.GetFullPath(worktreePath).TrimEnd('\\', '/');

        Step("[1/6] Tools");
        await AssertToolsAsync();
        Ok("git and svn are available.");

        Step("[2/6] SVN working copy");
        await EnsureWorkingCopyAsync(svnUrl);

        Step("[3/6] git repo");
        await EnsureGitRepoAsync();

        Step("[4/6] .gitignore");
        await EnsureGitIgnoreAsync();

        Step($"[5/6] Record SVN on git '{_mirrorBranch}'");
        await SyncMainWithSvnAsync(dryRun: false);

        Step($"[6/6] Branch '{_branch}' in its own worktree folder");
        var worktree = await EnsureWorktreeAsync(worktreePath);

        Done($"Done. {_wc} mirrors SVN on git '{_mirrorBranch}'; do your work on '{_branch}' in {worktree}.");
        Done("Commit on the branch, then use the SVN tab: Send to SVN (it syncs back after the SVN commit).");
        return worktree;
    }

    private async Task EnsureWorkingCopyAsync(string? svnUrl)
    {
        if (Svn.IsWorkingCopy(_wc))
        {
            var url = (await SvnAsync(new[] { "info", "--show-item", "url" })).StdOut.Trim();
            if (!string.IsNullOrWhiteSpace(svnUrl) && !UrlsEqual(url, svnUrl))
                throw Stop($"{_wc} is already a working copy of {url}, not {svnUrl}.",
                    new[] { "Pick another folder for the new checkout, or leave the URL empty to use this working copy." });

            var root = (await SvnAsync(new[] { "info", "--show-item", "wc-root" })).StdOut.Trim().TrimEnd('\\', '/');
            if (!string.Equals(Path.GetFullPath(root), _wc, StringComparison.OrdinalIgnoreCase))
                throw Stop($"{_wc} is inside the SVN working copy {root}; pick the working copy's top folder.");
            Ok($"Using the existing working copy of {url} at r{await GetSvnRevisionAsync()}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(svnUrl))
            throw Stop($"{_wc} is not an SVN working copy.", new[] { "Enter the SVN URL to check out into it, or pick a folder that already is a working copy." });
        if (Directory.Exists(_wc) && Directory.EnumerateFileSystemEntries(_wc).Any())
            throw Stop($"{_wc} already exists and is not empty, so SVN can't check out into it.");

        var parent = Path.GetDirectoryName(_wc) ?? throw Stop($"{_wc} has no parent folder.");
        Directory.CreateDirectory(parent);
        Info($"Checking out {svnUrl.Trim()} - a big repo can take several minutes...");
        _footer = "If the checkout stopped part-way, delete the folder and run Add SVN repo again.";
        var checkout = await Svn.RunAsync(parent, new[] { "checkout", "--non-interactive", svnUrl.Trim(), _wc }, Svn.UpdateTimeout, _ct);
        if (!checkout.Success)
            throw Stop($"'svn checkout' failed (exit {checkout.ExitCode}).", new[]
            {
                checkout.CombinedOutput,
                "If SVN needs your password, run 'svn info <url>' once in a terminal so the login is saved - the app runs svn non-interactively.",
            });
        _footer = SetupFooter;
        Ok($"Checked out r{await GetSvnRevisionAsync()} into {_wc}.");
    }

    private static bool UrlsEqual(string a, string b) =>
        string.Equals(Uri.UnescapeDataString(a.Trim().TrimEnd('/')), Uri.UnescapeDataString(b.Trim().TrimEnd('/')), StringComparison.OrdinalIgnoreCase);

    private async Task EnsureGitRepoAsync()
    {
        if (!(await GitAsync(new[] { "check-ref-format", "--branch", _branch }, allowFail: true)).Success || MirrorBranches.Contains(_branch))
            throw Stop($"'{_branch}' can't be the work branch name.", new[] { "Use a plain git branch name other than main/master, e.g. 'work'." });

        var gitPath = Path.Combine(_wc, ".git");
        if (File.Exists(gitPath)) throw Stop($"{gitPath} is a file: this folder is a git worktree or submodule, not a main repo.");
        if (Directory.Exists(gitPath)) Ok("git repo already exists.");
        else
        {
            await GitAsync(new[] { "init", "-q", "-b", "main" });
            Ok("Created a git repo on branch 'main'.");
        }

        var current = (await GitAsync(new[] { "branch", "--show-current" })).StdOut.Trim();
        if (!MirrorBranches.Contains(current))
            throw Stop($"git in {_wc} is on '{current}', not 'main'. The working copy's git must stay on main, the SVN mirror.",
                new[] { $"Switch it back first:  git -C \"{_wc}\" switch main" });
        _mirrorBranch = current;

        if ((await GitAsync(new[] { "config", "core.autocrlf" }, allowFail: true)).StdOut.Trim() == "false")
            Ok("core.autocrlf is already false.");
        else
        {
            await GitAsync(new[] { "config", "core.autocrlf", "false" });
            Ok("Set core.autocrlf=false so files reach SVN byte for byte.");
        }
    }

    /// <summary>.svn must never get into git. svn:ignore rules become git rules too, so build output and local
    /// files in the worktree don't show up as new files (anything committed on the branch is svn added by Send).</summary>
    private async Task EnsureGitIgnoreAsync()
    {
        var file = Path.Combine(_wc, ".gitignore");
        if (File.Exists(file))
        {
            var lines = (await File.ReadAllLinesAsync(file, _ct)).Select(l => l.Trim());
            if (lines.Any(l => l is ".svn" or ".svn/" or "/.svn" or "/.svn/"))
            {
                Ok(".gitignore already excludes .svn.");
                return;
            }
            await File.AppendAllTextAsync(file, "\n# SVN's own folder - never part of git\n.svn/\n", new UTF8Encoding(false), _ct);
            Ok("Added .svn/ to the existing .gitignore.");
            return;
        }

        var rules = await GetSvnIgnoreRulesAsync();
        var sb = new StringBuilder();
        sb.Append("# Written by repo-sync when this SVN working copy was set up as a git mirror.\n");
        sb.Append("# SVN's own folder - never part of git.\n.svn/\n");
        if (rules.Count > 0)
        {
            sb.Append("\n# From svn:ignore / svn:global-ignores\n");
            foreach (var rule in rules) sb.Append(rule).Append('\n');
        }
        await File.WriteAllTextAsync(file, sb.ToString(), new UTF8Encoding(false), _ct);
        Ok($"Wrote .gitignore (.svn/ and {rules.Count} rule(s) from svn:ignore).");
    }

    private async Task<List<string>> GetSvnIgnoreRulesAsync()
    {
        var rules = new List<string>();
        foreach (var property in new[] { "svn:ignore", "svn:global-ignores" })
        {
            var result = await SvnAsync(new[] { "propget", property, "-R", "--xml", "." }, allowFail: true);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) continue;

            foreach (var target in XDocument.Parse(result.StdOut).Descendants("target"))
            {
                var dir = target.Attribute("path")?.Value ?? ".";
                if (Path.IsPathRooted(dir)) dir = Path.GetRelativePath(_wc, dir);
                dir = dir.Replace('\\', '/').Trim('/');
                if (dir == ".") dir = string.Empty;

                var value = target.Elements("property").FirstOrDefault()?.Value ?? string.Empty;
                foreach (var raw in value.Replace("\r\n", "\n").Split('\n'))
                {
                    var pattern = raw.Trim();
                    if (pattern.Length == 0) continue;

                    // svn:ignore matches the folder's own children only; svn:global-ignores matches at any depth below it.
                    var rule = property == "svn:ignore"
                        ? (dir.Length == 0 ? $"/{pattern}" : $"/{dir}/{pattern}")
                        : (dir.Length == 0 ? pattern : $"/{dir}/**/{pattern}");
                    if (rule[0] is '#' or '!') rule = "\\" + rule;
                    rules.Add(rule);
                }
            }
        }
        return rules.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<string> EnsureWorktreeAsync(string worktreePath)
    {
        if ((await GitAsync(new[] { "rev-parse", "--verify", "-q", $"refs/heads/{_branch}" }, allowFail: true)).Success)
            Ok($"Branch '{_branch}' already exists.");
        else
        {
            await GitAsync(new[] { "branch", _branch, _mirrorBranch });
            Ok($"Created branch '{_branch}' from '{_mirrorBranch}'.");
        }

        var existing = (await Git.GetWorktreesAsync(_wc, _ct)).FirstOrDefault(w => !w.IsMain && w.Branch == _branch && !w.Prunable)?.Path;
        if (existing is not null)
        {
            Ok($"'{_branch}' is already checked out in {existing}.");
            return existing;
        }

        if (Directory.Exists(worktreePath) && Directory.EnumerateFileSystemEntries(worktreePath).Any())
            throw Stop($"{worktreePath} already exists and is not empty.", new[] { "Pick another work folder." });
        await GitAsync(new[] { "worktree", "add", worktreePath, _branch });
        Ok($"Checked out '{_branch}' in {worktreePath}.");
        return worktreePath;
    }

    // ---------------------------------------------------------------------------------------------
    // MOVE WORK FOLDER
    // ---------------------------------------------------------------------------------------------

    /// <summary>Renames/moves the branch's worktree folder with 'git worktree move', so git's own records follow it
    /// (renaming in Explorer would leave git pointing at the old folder). <paramref name="newPathOrName"/> is a full
    /// path, or just a folder name to rename it in place. Returns the new folder.</summary>
    public async Task<string> MoveWorktreeAsync(string newPathOrName)
    {
        var current = await AssertSetupAsync();
        _footer = "The work folder was not moved.";

        var target = newPathOrName.Trim().Trim('"').TrimEnd('\\', '/');
        if (target.Length == 0) throw Stop("No new name was given.");
        if (!Path.IsPathFullyQualified(target))
        {
            if (target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw Stop($"'{target}' isn't a valid folder name.", new[] { "Give a plain folder name, or a full path like C:\\code\\my-repo-work." });
            target = Path.Combine(Path.GetDirectoryName(current) ?? throw Stop($"{current} has no parent folder."), target);
        }
        target = Path.GetFullPath(target);

        Step($"Move work folder '{_branch}'");
        Info($"From: {current}");
        Info($"To  : {target}");

        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
        {
            Ok("That's already where it is - nothing to do.");
            return current;
        }
        if (string.Equals(target, _wc, StringComparison.OrdinalIgnoreCase) || target.StartsWith(_wc + "\\", StringComparison.OrdinalIgnoreCase))
            throw Stop("The work folder must be outside the SVN working copy (SVN would see it as a new folder).");
        if (Directory.Exists(target) || File.Exists(target)) throw Stop($"{target} already exists.", new[] { "Pick a name that isn't taken." });
        if (Path.GetDirectoryName(target) is { } parent) Directory.CreateDirectory(parent);

        var move = await GitAsync(new[] { "worktree", "move", current, target }, allowFail: true);
        if (!move.Success)
            throw Stop("'git worktree move' failed.", new[]
            {
                move.CombinedOutput,
                "Windows can't move a folder while a program has files open in it: close VS Code, terminals and any dev server running from the work folder, then try again.",
            });
        Ok($"Moved. Do your work in {target} now (reopen it in VS Code).");
        return target;
    }
}
