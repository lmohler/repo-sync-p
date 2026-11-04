using System.IO;
using System.Text;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// Every change the app makes to a repo, as plain git commands. Each command is written to the activity log, so
/// the log doubles as a record of exactly what was run. Unsafe states (a merge in progress, uncommitted work
/// where it would be lost) stop with a <see cref="UserFacingException"/> that says what to do instead.
/// </summary>
public sealed class RepoActions
{
    private readonly string _path;
    private readonly IProgress<LogEntry> _log;
    private readonly CancellationToken _ct;

    public RepoActions(string path, IProgress<LogEntry> log, CancellationToken ct = default)
    {
        _path = path;
        _log = log;
        _ct = ct;
    }

    /// <summary>Run before every push (the repo's setting); a non-zero exit stops the push unless <see cref="PushAnyway"/> says go.</summary>
    public string? PrePushCommand { get; init; }

    /// <summary>Gets git's progress lines ("Receiving objects: 42% ...") during fetch / pull / push / clone, on a background thread.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>Asked with the failed check's output tail; true pushes anyway. Null = a failed check always stops.</summary>
    public Func<string, bool>? PushAnyway { get; init; }

    private void Info(string text, LogLevel level = LogLevel.Info) => _log.Report(new LogEntry(text, level));

    private async Task RunPrePushCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(PrePushCommand)) return;
        Info($"Pre-push check: {PrePushCommand}", LogLevel.Command);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = _path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(PrePushCommand);
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new UserFacingException("Could not start the pre-push check.");
        var outTask = process.StandardOutput.ReadToEndAsync(_ct);
        var errTask = process.StandardError.ReadToEndAsync(_ct);
        try
        {
            await process.WaitForExitAsync(_ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* already gone */ }
            throw;
        }
        var lines = ((await outTask) + "\n" + (await errTask)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.TakeLast(25).ToArray();
        foreach (var line in tail) Info("  " + line, LogLevel.Command);
        if (process.ExitCode == 0)
        {
            Info("Pre-push check passed.", LogLevel.Success);
            return;
        }
        var summary = string.Join("\n", lines.TakeLast(8));
        if (PushAnyway?.Invoke(summary) == true)
        {
            Info($"Pre-push check failed (exit {process.ExitCode}) - pushing anyway as asked.", LogLevel.Warning);
            return;
        }
        throw new UserFacingException($"Pre-push check failed (exit {process.ExitCode}): {PrePushCommand}",
            summary, "Fix it and push again. The full output is in the Activity panel; the check is set in Repo settings.");
    }

    /// <summary>Runs git, logs the command and its output, and throws with git's message when it fails.</summary>
    private async Task<GitResult> GitAsync(params string[] args) => await GitAsync(args, network: false);

    private async Task<GitResult> GitAsync(IReadOnlyList<string> args, bool network, string? failMessage = null, params string[] help)
    {
        // network transfers report their progress (git only prints it to a terminal unless asked)
        var report = network && Progress is not null && args.Count > 0 && args[0] is "fetch" or "push" or "pull" or "clone";
        if (report) args = new[] { args[0], "--progress" }.Concat(args.Skip(1)).ToArray();
        Info("git " + string.Join(' ', args.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a)), LogLevel.Command);
        var result = await Git.RunAsync(_path, args, _ct, network ? Git.NetworkTimeout : null, onProgress: report ? Progress : null);
        foreach (var line in result.CombinedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40))
            Info("  " + line, LogLevel.Command);
        if (!result.Success)
            throw new UserFacingException(failMessage ?? $"'git {args[0]}' failed.", new[] { result.CombinedOutput }.Concat(help).ToArray());
        return result;
    }

    private async Task<string> RemoteAsync() =>
        await Git.GetRemoteNameAsync(_path, _ct) ?? throw new UserFacingException("This repo has no remote.",
            "Add one with the instructions on the project's GitHub / GitLab page, or clone the project with Clone.");

    private async Task<RepoStatus> StatusAsync()
    {
        var status = await Git.GetStatusAsync(_path, _ct);
        if (!status.Ok) throw new UserFacingException("Could not read the repo's status.", status.Error ?? string.Empty);
        return status;
    }

    private async Task<string> RequireBranchAsync()
    {
        var status = await StatusAsync();
        if (status.Detached) throw new UserFacingException("Not on a branch (detached HEAD).", "Switch to a branch on the Branches tab first.");
        return status.Branch;
    }

    private void EnsureNoMerge()
    {
        if (Git.IsMergeInProgress(_path) || Git.IsRebaseInProgress(_path))
            throw new UserFacingException("A merge or rebase is in progress here.",
                "Finish it on the Changes tab (resolve the conflicts, then Continue), or click Abort to back out.");
    }

    private async Task EnsureCleanAsync(string what)
    {
        var status = await StatusAsync();
        if (status.IsDirty)
            throw new UserFacingException($"There are {status.Staged.Count + status.Unstaged.Count + status.Conflicted.Count} uncommitted change(s) - commit or stash them before {what}.",
                "Commit on the Changes tab, or use Stash on the Stashes tab to put them aside.");
    }

    // ---------------------------------------------------------------------------------------------
    // Remote sync
    // ---------------------------------------------------------------------------------------------

    public async Task FetchAsync(bool quiet = false)
    {
        var remote = await RemoteAsync();
        if (quiet)
        {
            var r = await Git.RunAsync(_path, new[] { "fetch", remote, "--prune", "--quiet" }, _ct, Git.NetworkTimeout);
            if (!r.Success) throw new UserFacingException("Fetch failed.", r.CombinedOutput);
            return;
        }
        await GitAsync(new[] { "fetch", remote, "--prune" }, network: true, "Fetch failed.", "Check the network / VPN, and that you can sign in to the server.");
    }

    /// <summary>
    /// Brings the upstream's new commits in. Fast-forwards when possible; when both sides have new commits it
    /// rebases your local commits on top (so the history stays straight). Uncommitted changes are stashed
    /// for the duration and put back afterwards.
    /// </summary>
    public async Task<string> PullAsync()
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        var remote = await RemoteAsync();
        await GitAsync(new[] { "fetch", remote, "--prune" }, network: true, "Fetch failed.");

        var status = await StatusAsync();
        if (status.Upstream is null || status.UpstreamGone)
        {
            Info($"'{branch}' has no upstream on the server yet - nothing to pull. Push publishes it.", LogLevel.Warning);
            return "Nothing to pull (branch not published)";
        }
        if (status.Behind == 0)
        {
            Info($"'{branch}' is up to date with {status.Upstream}.", LogLevel.Success);
            return "Already up to date";
        }

        var stashed = false;
        if (status.IsDirty)
        {
            if (status.Conflicted.Count > 0) throw new UserFacingException("Resolve the conflicted files first (Changes tab).");
            await GitAsync("stash", "push", "--include-untracked", "-m", "repo-sync: auto-stash before pull");
            stashed = true;
        }

        try
        {
            if (status.Ahead == 0)
            {
                await GitAsync("merge", "--ff-only", status.Upstream);
            }
            else
            {
                Info($"Both sides have new commits: putting your {status.Ahead} local commit(s) on top of {status.Upstream}.");
                var rebase = await Git.RunAsync(_path, new[] { "rebase", status.Upstream }, _ct);
                Info($"git rebase {status.Upstream}", LogLevel.Command);
                if (!rebase.Success)
                {
                    if (Git.IsRebaseInProgress(_path))
                    {
                        throw new UserFacingException(
                            "Your commits conflict with the server's - the rebase is paused so you can resolve it.",
                            "Open the Changes tab: pick Keep mine / Take theirs (or edit the file), mark each resolved, then click Continue.",
                            "Or click Abort to put everything back the way it was.",
                            stashed ? "Your uncommitted changes are saved in the stash list (Stashes tab) - apply them after." : string.Empty);
                    }
                    throw new UserFacingException("'git rebase' failed.", rebase.CombinedOutput);
                }
            }
        }
        finally
        {
            if (stashed && !Git.IsRebaseInProgress(_path))
            {
                var pop = await Git.RunAsync(_path, new[] { "stash", "pop" }, _ct);
                Info("git stash pop", LogLevel.Command);
                if (!pop.Success)
                    Info("Your uncommitted changes conflicted with the pulled commits; they are still in the stash list (Stashes tab).", LogLevel.Warning);
            }
        }

        Info($"Pulled {status.Behind} commit(s) into '{branch}'.", LogLevel.Success);
        return $"Pulled {status.Behind} commit(s)";
    }

    /// <summary>Pushes the current branch; publishes it with -u the first time.</summary>
    public async Task<string> PushAsync()
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        var remote = await RemoteAsync();
        var status = await StatusAsync();
        await RunPrePushCheckAsync();
        await CheckLargeFilesAsync(status.Upstream is null || status.UpstreamGone ? null : status.Upstream);

        if (status.Upstream is null || status.UpstreamGone)
        {
            await GitAsync(new[] { "push", "-u", remote, branch }, network: true, "Push failed.");
            Info($"Published '{branch}' to {remote}.", LogLevel.Success);
            return $"Published {branch}";
        }
        if (status.Ahead == 0)
        {
            Info("Nothing to push.", LogLevel.Success);
            return "Nothing to push";
        }
        await GitAsync(new[] { "push", remote, branch }, network: true, "Push failed - the server may have commits you don't have.",
            "Click Pull first (it puts your commits on top of the server's), then Push again.");
        Info($"Pushed {status.Ahead} commit(s) to {remote}/{branch}.", LogLevel.Success);
        return $"Pushed {status.Ahead} commit(s)";
    }

    /// <summary>GitHub refuses files over 100 MB and GitLab often caps lower; stop before a push that would
    /// fail (or bloat the repo for everyone), and warn from 25 MB. LFS-tracked files are only pointers, so they pass.</summary>
    private async Task CheckLargeFilesAsync(string? upstream)
    {
        var range = upstream is not null ? $"{upstream}..HEAD" : null;
        if (range is null)
        {
            if (await Git.GetRemoteNameAsync(_path, _ct) is { } remote)
            {
                var def = $"{remote}/{await Git.GetDefaultBranchAsync(_path, remote, _ct)}";
                if (await Git.RefExistsAsync(_path, $"refs/remotes/{def}", _ct)) range = $"{def}..HEAD";
            }
        }
        var big = await Git.GetLargeFilesAsync(_path, 25L * 1024 * 1024, range, _ct);
        if (big.Count == 0) return;
        foreach (var (file, bytes) in big) Info($"Large file in this push: {file} ({bytes / 1024 / 1024} MB)", LogLevel.Warning);
        var tooBig = big.Where(f => f.Bytes > 100L * 1024 * 1024).ToList();
        if (tooBig.Count > 0)
            throw new UserFacingException($"{tooBig.Count} file(s) are over 100 MB - the server will reject this push.",
                string.Join("\n", tooBig.Select(f => $"{f.Path} ({f.Bytes / 1024 / 1024} MB)")),
                "Track them with Git LFS (git lfs track \"*.ext\"), or remove them: Undo last commit, add them to .gitignore, and commit again.");
    }

    /// <summary>git rebase -i onto, with the todo written by us (GIT_SEQUENCE_EDITOR copies it in). Uncommitted
    /// work is auto-stashed. A conflict leaves the rebase in progress for Continue / Abort on the Changes tab.</summary>
    public async Task InteractiveRebaseAsync(string onto, IReadOnlyList<RebaseStep> steps)
    {
        EnsureNoMerge();
        CleanFinishedRebaseTemp();
        var dir = Path.Combine(Path.GetTempPath(), RebaseTempPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var todo = Path.Combine(dir, "todo.txt");
        await File.WriteAllTextAsync(todo, InteractiveRebase.BuildTodo(steps, dir), new UTF8Encoding(false), _ct);
        var args = new[] { "rebase", "-i", "--autostash", onto };
        Info("git " + string.Join(' ', args), LogLevel.Command);
        var env = new Dictionary<string, string> { ["GIT_SEQUENCE_EDITOR"] = $"cp '{todo.Replace('\\', '/')}'" };
        var result = await Git.RunAsync(_path, args, _ct, env: env);
        foreach (var line in result.CombinedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40))
            Info("  " + line, LogLevel.Command);
        // a stopped rebase still needs the reword message files when you Continue, so the folder stays then;
        // the marker lets Continue / Abort remove it once the rebase is over
        if (!result.Success)
        {
            if (Git.IsRebaseInProgress(_path) && Git.GitDir(_path) is { } gitDir)
            {
                try { await File.WriteAllTextAsync(Path.Combine(gitDir, RebaseTempMarker), dir, _ct); } catch { /* the startup sweep gets it later */ }
            }
            throw new UserFacingException("The rebase stopped part way.", result.CombinedOutput,
                "Fix the conflicted files on the Changes tab and click Continue, or Abort to put everything back.");
        }
        Info($"Rewrote {steps.Count} commit(s).", LogLevel.Success);
        try { Directory.Delete(dir, recursive: true); } catch { /* temp folder */ }
    }

    private const string RebaseTempPrefix = "repo-sync-rebase-";
    private const string RebaseTempMarker = "repo-sync-rebase-dir";

    /// <summary>Deletes the message folder of an interactive rebase this app started that has since finished or been
    /// aborted (one that's still stopped needs it for Continue).</summary>
    private void CleanFinishedRebaseTemp()
    {
        if (Git.IsRebaseInProgress(_path) || Git.GitDir(_path) is not { } gitDir) return;
        var marker = Path.Combine(gitDir, RebaseTempMarker);
        if (!File.Exists(marker)) return;
        try
        {
            var dir = File.ReadAllText(marker).Trim();
            if (Path.GetFileName(dir).StartsWith(RebaseTempPrefix, StringComparison.Ordinal) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            File.Delete(marker);
        }
        catch
        {
            // temp folder; the startup sweep gets it later
        }
    }

    /// <summary>Startup sweep for rebase message folders left behind, e.g. by a rebase finished outside the app.</summary>
    public static void CleanOldRebaseTemp(TimeSpan olderThan)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), RebaseTempPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow - olderThan) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* in use */ }
            }
        }
        catch
        {
            // best effort
        }
    }

    public async Task<string> ForcePushAsync()
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        var remote = await RemoteAsync();
        var status = await StatusAsync();
        await RunPrePushCheckAsync();
        await CheckLargeFilesAsync(status.Upstream is null || status.UpstreamGone ? null : status.Upstream);

        Info($"Force-pushing '{branch}' to {remote}...", LogLevel.Warning);
        if (status.Upstream is null || status.UpstreamGone)
        {
            await GitAsync(new[] { "push", "--force-with-lease", "-u", remote, branch }, network: true, "Force push failed.");
            Info($"Published '{branch}' to {remote} (forced with lease).", LogLevel.Success);
            return $"Published {branch} (force)";
        }
        await GitAsync(new[] { "push", "--force-with-lease", remote, branch }, network: true, "Force push failed - someone else pushed new commits to the server.",
            "Use Fetch to see their commits before overwriting.");
        Info($"Force pushed '{branch}' to {remote}/{branch}.", LogLevel.Success);
        return $"Force pushed {branch}";
    }

    public async Task<string> SyncAsync()
    {
        var pulled = await PullAsync();
        var pushed = await PushAsync();
        return $"{pulled}; {pushed}";
    }

    // ---------------------------------------------------------------------------------------------
    // Working tree
    // ---------------------------------------------------------------------------------------------

    public Task StageAsync(IEnumerable<string> paths) => GitAsync(new[] { "add", "-A", "--" }.Concat(paths).ToArray());

    public Task StageAllAsync() => GitAsync("add", "-A");

    public async Task UnstageAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        // a repo with no commits yet has no HEAD to restore from
        if (await Git.RefExistsAsync(_path, "HEAD", _ct))
            await GitAsync(new[] { "restore", "--staged", "--" }.Concat(list).ToArray());
        else
            await GitAsync(new[] { "rm", "--cached", "-r", "-q", "--" }.Concat(list).ToArray());
    }

    public Task UnstageAllAsync() => UnstageAsync(new[] { "." });

    /// <summary>Throws away unstaged edits to tracked files and deletes untracked ones.</summary>
    public async Task DiscardAsync(IReadOnlyList<FileChange> changes)
    {
        var tracked = changes.Where(c => !c.IsUntracked).Select(c => c.Path).ToList();
        var untracked = changes.Where(c => c.IsUntracked).Select(c => c.Path).ToList();
        if (tracked.Count > 0) await GitAsync(new[] { "restore", "--worktree", "--" }.Concat(tracked).ToArray());
        if (untracked.Count > 0) await GitAsync(new[] { "clean", "-f", "-q", "--" }.Concat(untracked).ToArray());
    }

    /// <summary>Applies a <see cref="PartialPatch"/>: to the index (stage / unstage with reverse) or, with
    /// toWorktree, reverse-applied to the files (discard lines).</summary>
    public async Task ApplyPatchAsync(string patch, bool reverse, bool toWorktree = false)
    {
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, patch, new UTF8Encoding(false), _ct);
            var args = new List<string> { "apply", "--recount", "--whitespace=nowarn" };
            if (!toWorktree) args.Add("--cached");
            if (reverse || toWorktree) args.Add("-R");
            args.Add(temp);
            await GitAsync(args, network: false, "Could not apply the selected lines.", "The file may have changed since the diff was shown - refresh and try again.");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task CommitAsync(string message, bool amend)
    {
        if (string.IsNullOrWhiteSpace(message) && !amend) throw new UserFacingException("Write a commit message first.");
        if (Svn.IsWorkingCopy(_path))
            throw new UserFacingException("This folder is the SVN working copy - its git branch only records what SVN has committed.",
                "Commit your work on the work branch's card instead, then use the SVN tab to Send it. To record SVN here, use SVN tab > Sync from SVN.");
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, message.Trim() + "\n", new UTF8Encoding(false), _ct);
            var args = new List<string> { "commit", "-F", temp };
            if (amend) args.Add("--amend");
            if (amend && string.IsNullOrWhiteSpace(message)) args = new List<string> { "commit", "--amend", "--no-edit" };
            await GitAsync(args, network: false, "Commit failed.",
                "If it says your name/email is unknown, set them in Settings > Git identity.");
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
        Info(amend ? "Amended the last commit." : "Committed.", LogLevel.Success);
    }

    /// <summary>Takes the last commit back out (its changes stay staged). Only for commits not pushed yet.</summary>
    public async Task UndoLastCommitAsync()
    {
        EnsureNoMerge();
        var status = await StatusAsync();
        if (status.Upstream is not null && !status.UpstreamGone && status.Ahead == 0)
            throw new UserFacingException("The last commit is already on the server.",
                "Undoing it would rewrite shared history. Use Revert on the History tab instead - it adds a commit that undoes it.");
        await GitAsync("reset", "--soft", "HEAD~1");
        Info("Undid the last commit - its changes are staged on the Changes tab.", LogLevel.Success);
    }

    /// <summary>Puts HEAD and the local branches back where <paramref name="before"/> recorded them. Uncommitted
    /// work is kept (reset --keep refuses instead of overwriting it). Refuses when a commit that would be dropped is
    /// already on the server, and keeps a new branch that has commits of its own.</summary>
    public async Task UndoToAsync(UndoPoint before)
    {
        EnsureNoMerge();
        if (await UndoPoint.TakeAsync(_path, before.Title, _ct) is not { } now) throw new UserFacingException("Nothing to undo.");

        // commits reachable now that weren't before - none of them may be on the server
        var beforeTips = before.Heads.Values.Append(before.Head).Distinct().Select(h => "^" + h);
        var dropped = (await GitAsync(new[] { "rev-list", "HEAD", "--branches" }.Concat(beforeTips).ToArray())).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var hash in dropped)
            if ((await Git.RunAsync(_path, new[] { "branch", "-r", "--contains", hash }, _ct)).StdOut.Trim().Length > 0)
                throw new UserFacingException($"Can't undo '{before.Title}': its commits are already on the server.",
                    "Undoing would rewrite shared history. Use Revert on the History tab instead.");

        // back to the branch (or detached commit) we were on
        if (before.Branch is null)
        {
            if (now.Branch is not null || now.Head != before.Head) await GitAsync("switch", "--detach", before.Head);
        }
        else if (now.Branch != before.Branch)
        {
            if (now.Heads.ContainsKey(before.Branch)) await GitAsync("switch", before.Branch);
            else await GitAsync("switch", "-c", before.Branch, before.Heads[before.Branch]);
        }
        if (before.Branch is not null && before.Heads.TryGetValue(before.Branch, out var tip)
            && (await Git.RunAsync(_path, new[] { "rev-parse", "HEAD" }, _ct)).StdOut.Trim() != tip)
        {
            // New plain commits on top (commit, cherry-pick, revert): take them back but keep their changes staged,
            // like Undo last commit. Anything else (merge, rebase, reset) moves back with --keep - that content
            // still lives in another commit.
            var onTop = now.Branch == before.Branch
                && (await Git.RunAsync(_path, new[] { "merge-base", "--is-ancestor", tip, "HEAD" }, _ct)).Success
                && (await GitAsync("rev-list", "--merges", tip + "..HEAD")).StdOut.Trim().Length == 0;
            await GitAsync(new[] { "reset", onTop ? "--soft" : "--keep", tip }, false, "Could not move the branch back.",
                "Uncommitted changes touch the same files. Commit or stash them, then try Undo again.");
            if (onTop) Info("The undone commits' changes are staged on the Changes tab.");
        }

        // the other branches
        foreach (var (name, hash) in before.Heads.Where(kv => kv.Key != before.Branch))
            if (!now.Heads.TryGetValue(name, out var cur) || cur != hash) await GitAsync("branch", "-f", name, hash);
        foreach (var name in now.Heads.Keys.Where(n => !before.Heads.ContainsKey(n)))
        {
            var own = (await GitAsync(new[] { "rev-list", name }.Concat(beforeTips).ToArray())).StdOut.Trim();
            if (own.Length == 0) await GitAsync("branch", "-D", name);
            else Info($"Kept branch '{name}' - it has commits of its own.", LogLevel.Warning);
        }
        Info($"Undid '{before.Title}'.", LogLevel.Success);
    }

    // ---------------------------------------------------------------------------------------------
    // Merge / rebase in progress
    // ---------------------------------------------------------------------------------------------

    /// <summary>Resolves a conflicted file with one side. "Mine" is the branch you're on; during a rebase git
    /// swaps ours/theirs, which this hides.</summary>
    public async Task ResolveAsync(string file, bool keepMine)
    {
        var rebase = Git.IsRebaseInProgress(_path);
        var side = keepMine ^ rebase ? "--ours" : "--theirs";
        await GitAsync("checkout", side, "--", file);
        await GitAsync("add", "--", file);
    }

    public Task MarkResolvedAsync(string file) => GitAsync("add", "--", file);

    public async Task ContinueAsync()
    {
        var status = await StatusAsync();
        if (status.Conflicted.Count > 0)
            throw new UserFacingException($"{status.Conflicted.Count} file(s) still have conflicts.", "Resolve each one, then Continue.");

        if (Git.IsRebaseInProgress(_path))
        {
            await GitAsync("rebase", "--continue");
            if (Git.IsRebaseInProgress(_path))
            {
                Info("The next commit in the rebase has conflicts too - resolve them and Continue again.", LogLevel.Warning);
                return;
            }
            CleanFinishedRebaseTemp();
            Info("Rebase finished.", LogLevel.Success);
            return;
        }
        if (Git.IsMergeInProgress(_path))
        {
            await GitAsync("commit", "--no-edit");
            Info("Merge committed. Push to share it.", LogLevel.Success);
            return;
        }
        throw new UserFacingException("No merge or rebase is in progress.");
    }

    public async Task AbortAsync()
    {
        var gitDir = Git.GitDir(_path) ?? string.Empty;
        if (Git.IsRebaseInProgress(_path)) await GitAsync("rebase", "--abort");
        else if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) await GitAsync("cherry-pick", "--abort");
        else if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) await GitAsync("revert", "--abort");
        else if (Git.IsMergeInProgress(_path)) await GitAsync("merge", "--abort");
        else throw new UserFacingException("No merge or rebase is in progress.");
        CleanFinishedRebaseTemp();
        Info("Aborted - the branch is back where it was.", LogLevel.Success);
    }

    // ---------------------------------------------------------------------------------------------
    // Branches (the feature-branch loop)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Applies the branch pattern: {name} = the typed name (spaces become dashes), {user} = git user.name.</summary>
    public static string BuildBranchName(string pattern, string name, string user)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "feature/{name}-{user}";
        var cleanName = string.Join("-", name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!pattern.Contains("{name}")) pattern += "{name}";
        return pattern.Replace("{name}", cleanName).Replace("{user}", user);
    }

    /// <summary>git config user.name reduced to something that fits in a branch name.</summary>
    public static async Task<string> GetBranchUserAsync(string path, CancellationToken ct = default)
    {
        var name = (await Git.RunAsync(path, new[] { "config", "user.name" }, ct)).StdOut.Trim();
        if (name.Length == 0) name = Environment.UserName;
        var safe = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return safe.Trim('-', '.');
    }

    /// <summary>Fetches, then creates the branch from <paramref name="startPoint"/> (usually origin/main) and switches
    /// to it. Uncommitted changes come along. --no-track so a plain push never goes to main.</summary>
    public async Task StartBranchAsync(string branch, string startPoint, bool publish)
    {
        EnsureNoMerge();
        if (!await Git.IsValidBranchNameAsync(_path, branch, _ct))
            throw new UserFacingException($"'{branch}' isn't a valid branch name.", "Avoid spaces, '..', '~', '^', ':' and a trailing '/' or '.lock'.");
        if (await Git.RefExistsAsync(_path, $"refs/heads/{branch}", _ct))
            throw new UserFacingException($"A branch named '{branch}' already exists.", "Switch to it on the Branches tab.");

        // A repo with no remote (e.g. an SVN working copy's local git) branches from a local ref and skips fetch / publish.
        var remote = await Git.GetRemoteNameAsync(_path, _ct);
        if (remote is not null && startPoint.StartsWith(remote + "/", StringComparison.Ordinal))
            await GitAsync(new[] { "fetch", remote, "--prune" }, network: true, "Fetch failed.");
        if (!await Git.RefExistsAsync(_path, startPoint, _ct))
            throw new UserFacingException($"'{startPoint}' doesn't exist in this repo.", "Pick another branch to start from.");
        await GitAsync("switch", "-c", branch, "--no-track", startPoint);
        if (publish && remote is not null)
            await GitAsync(new[] { "push", "-u", remote, branch }, network: true, "Created the branch, but publishing it failed.");
        Info($"On new branch '{branch}' (from {startPoint}).", LogLevel.Success);
    }

    /// <summary>Switches to a local branch, or to a remote one ("origin/x" creates a local "x" tracking it).</summary>
    public async Task SwitchAsync(string branch)
    {
        EnsureNoMerge();
        var remote = await Git.GetRemoteNameAsync(_path, _ct);
        var local = remote is not null && branch.StartsWith(remote + "/", StringComparison.Ordinal) ? branch[(remote.Length + 1)..] : branch;

        if (await Git.RefExistsAsync(_path, $"refs/heads/{local}", _ct))
            await GitAsync(new[] { "switch", local }, false, $"Could not switch to '{local}'.",
                "If your uncommitted changes would be overwritten, commit or stash them first (Stashes tab).");
        else
            await GitAsync(new[] { "switch", "-c", local, "--track", $"{remote}/{local}" }, false, $"Could not check out '{branch}'.");
        Info($"Switched to '{local}'.", LogLevel.Success);
    }

    public async Task DeleteBranchAsync(string branch, bool force)
    {
        var status = await StatusAsync();
        if (status.Branch == branch) throw new UserFacingException("You can't delete the branch you're on.", "Switch to another branch first.");
        await GitAsync(new[] { "branch", force ? "-D" : "-d", branch }, false, $"Could not delete '{branch}'.",
            "It has commits that aren't merged anywhere. Delete it with 'force' only if you're sure you don't need them.");
        Info($"Deleted local branch '{branch}'.", LogLevel.Success);
    }

    public async Task DeleteRemoteBranchAsync(string remoteBranch)
    {
        var remote = await RemoteAsync();
        var name = remoteBranch.StartsWith(remote + "/", StringComparison.Ordinal) ? remoteBranch[(remote.Length + 1)..] : remoteBranch;
        await GitAsync(new[] { "push", remote, "--delete", name }, network: true, $"Could not delete '{name}' on the server.",
            "Protected branches can't be deleted, and you need Developer access or more.");
        Info($"Deleted '{name}' on {remote}.", LogLevel.Success);
    }

    /// <summary>Deletes local branches whose work is in the default branch (by history) or whose upstream was
    /// deleted on the server (GitHub / GitLab remove merged source branches). Never the current or the default branch.</summary>
    public async Task<int> CleanupMergedAsync(IReadOnlyList<string> branches)
    {
        var deleted = 0;
        foreach (var b in branches)
        {
            var r = await Git.RunAsync(_path, new[] { "branch", "-D", b }, _ct);
            Info($"git branch -D {b}", LogLevel.Command);
            if (r.Success) deleted++;
            else Info("  " + r.CombinedOutput, LogLevel.Warning);
        }
        Info($"Cleaned up {deleted} branch(es).", LogLevel.Success);
        return deleted;
    }

    public async Task RenameBranchAsync(string oldName, string newName)
    {
        if (!await Git.IsValidBranchNameAsync(_path, newName, _ct)) throw new UserFacingException($"'{newName}' isn't a valid branch name.");
        await GitAsync("branch", "-m", oldName, newName);
    }

    /// <summary>Fetches and merges the remote's default branch into the current branch. On a conflict the merge is
    /// left open so it can be resolved on the Changes tab, or backed out with Abort.</summary>
    public async Task<string> MergeBranchAsync(string sourceBranch, bool noFf = false)
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        await EnsureCleanAsync($"merging '{sourceBranch}'");

        Info($"Merging '{sourceBranch}' into '{branch}'...");
        var args = new List<string> { "merge" };
        if (noFf) args.Add("--no-ff");
        args.AddRange(new[] { "--no-edit", sourceBranch });

        var merge = await Git.RunAsync(_path, args, _ct);
        Info($"git {string.Join(' ', args)}", LogLevel.Command);
        if (merge.Success)
        {
            Info($"Merged '{sourceBranch}' into '{branch}'.", LogLevel.Success);
            return $"Merged '{sourceBranch}'";
        }
        if (Git.IsMergeInProgress(_path))
        {
            throw new UserFacingException($"'{sourceBranch}' conflicts with '{branch}' - the merge is paused for you to resolve.",
                "Open the Changes tab: pick Keep mine / Take theirs per file (or edit it), then click Continue.",
                "Or click Abort to back out.");
        }
        throw new UserFacingException("'git merge' failed before starting.", merge.CombinedOutput);
    }

    public async Task<string> RebaseOntoAsync(string upstreamBranch)
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        await EnsureCleanAsync($"rebasing onto '{upstreamBranch}'");

        Info($"Rebasing '{branch}' onto '{upstreamBranch}'...");
        var rebase = await Git.RunAsync(_path, new[] { "rebase", upstreamBranch }, _ct);
        Info($"git rebase {upstreamBranch}", LogLevel.Command);
        if (rebase.Success)
        {
            Info($"Rebased '{branch}' onto '{upstreamBranch}'.", LogLevel.Success);
            return $"Rebased onto '{upstreamBranch}'";
        }
        if (Git.IsRebaseInProgress(_path))
        {
            throw new UserFacingException(
                $"Your commits conflict with '{upstreamBranch}' - the rebase is paused so you can resolve it.",
                "Open the Changes tab: pick Keep mine / Take theirs (or edit the file), mark each resolved, then click Continue.",
                "Or click Abort to put everything back the way it was.");
        }
        throw new UserFacingException("'git rebase' failed.", rebase.CombinedOutput);
    }

    public async Task<string> UpdateFromDefaultAsync()
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        await EnsureCleanAsync("bringing in the default branch");

        var remote = await RemoteAsync();
        await GitAsync(new[] { "fetch", remote, "--prune" }, network: true, "Fetch failed.");
        var target = await Git.GetDefaultBranchAsync(_path, remote, _ct);
        var source = $"{remote}/{target}";

        var count = await Git.CountAsync(_path, $"HEAD..{source}", _ct);
        if (count == 0)
        {
            Info($"'{branch}' already has everything from {source}.", LogLevel.Success);
            return $"Already up to date with {source}";
        }

        Info($"Merging {count} commit(s) from {source} into '{branch}'...");
        Info($"git merge --no-edit {source}", LogLevel.Command);
        var merge = await Git.RunAsync(_path, new[] { "merge", "--no-edit", source }, _ct);
        if (merge.Success)
        {
            Info($"Merged {source} into '{branch}'. Push to update the merge request.", LogLevel.Success);
            return $"Merged {count} commit(s) from {source}";
        }
        if (Git.IsMergeInProgress(_path))
        {
            throw new UserFacingException($"{source} conflicts with '{branch}' - the merge is paused for you to resolve.",
                "Open the Changes tab: pick Keep mine / Take theirs per file (or edit it), then click Continue.",
                "Or click Abort to back out.");
        }
        throw new UserFacingException("'git merge' failed before starting.", merge.CombinedOutput);
    }

    /// <summary>
    /// After the merge request is merged: switch to the default branch, fast-forward it, and delete the feature
    /// branch locally and (if still there) on the server. Refuses unless the work is in the default branch - by
    /// history, or because the server says the merge request / pull request was merged (squash merges can't be matched by history).
    /// </summary>
    public async Task<string> FinishBranchAsync(bool mergedPerServer)
    {
        EnsureNoMerge();
        var branch = await RequireBranchAsync();
        await EnsureCleanAsync("finishing the branch");

        var remote = await RemoteAsync();
        await GitAsync(new[] { "fetch", remote, "--prune" }, network: true, "Fetch failed.");
        var target = await Git.GetDefaultBranchAsync(_path, remote, _ct);
        if (branch == target) throw new UserFacingException($"You're on '{target}' itself - there is no feature branch to finish.");

        var inHistory = (await Git.RunAsync(_path, new[] { "merge-base", "--is-ancestor", "HEAD", $"{remote}/{target}" }, _ct)).Success;
        if (!inHistory && !mergedPerServer)
        {
            throw new UserFacingException($"'{branch}' isn't merged into {remote}/{target} yet - nothing was changed.",
                "Merge its merge request / pull request first (on its tab or on the web), then Finish again.");
        }

        await GitAsync("switch", target);
        await GitAsync("merge", "--ff-only", $"{remote}/{target}");
        await GitAsync("branch", inHistory ? "-d" : "-D", branch);

        var onRemote = await Git.RunAsync(_path, new[] { "ls-remote", "--exit-code", "--heads", remote, branch }, _ct, Git.NetworkTimeout);
        if (onRemote.Success)
        {
            await GitAsync(new[] { "push", remote, "--delete", branch }, network: true);
            Info($"Deleted '{branch}' locally and on {remote}; now on an up-to-date '{target}'.", LogLevel.Success);
            return $"Finished '{branch}' (local and server branch deleted)";
        }
        Info($"Deleted '{branch}' (the server had already removed its copy); now on an up-to-date '{target}'.", LogLevel.Success);
        return $"Finished '{branch}'";
    }

    // ---------------------------------------------------------------------------------------------
    // History
    // ---------------------------------------------------------------------------------------------

    public async Task RevertAsync(string hash, bool isMerge)
    {
        EnsureNoMerge();
        await EnsureCleanAsync("reverting a commit");
        var args = isMerge ? new[] { "revert", "--no-edit", "-m", "1", hash } : new[] { "revert", "--no-edit", hash };
        await GitAsync(args, false, "Revert stopped.", "If it reports conflicts, resolve them on the Changes tab and click Continue, or Abort.");
        Info("Added a commit that undoes it. Push to share.", LogLevel.Success);
    }

    public async Task CherryPickAsync(string hash)
    {
        EnsureNoMerge();
        await EnsureCleanAsync("cherry-picking");
        await GitAsync(new[] { "cherry-pick", hash }, false, "Cherry-pick stopped.",
            "If it reports conflicts, resolve them on the Changes tab and click Continue, or Abort.");
    }

    public async Task CreateBranchAtAsync(string name, string hash)
    {
        if (!await Git.IsValidBranchNameAsync(_path, name, _ct)) throw new UserFacingException($"'{name}' isn't a valid branch name.");
        await GitAsync("branch", name, hash);
        Info($"Created branch '{name}' at {hash[..Math.Min(8, hash.Length)]}.", LogLevel.Success);
    }

    public async Task ResetAsync(string hash, string mode)
    {
        EnsureNoMerge();
        mode = mode.ToLowerInvariant() switch
        {
            "soft" => "soft",
            "hard" => "hard",
            "keep" => "keep",
            _ => "mixed",
        };
        if (mode == "hard")
        {
            Info($"Hard reset to {hash[..Math.Min(8, hash.Length)]} (discarding working tree changes)...", LogLevel.Warning);
        }
        await GitAsync("reset", $"--{mode}", hash);
        Info($"Reset branch to {hash[..Math.Min(8, hash.Length)]} ({mode}).", LogLevel.Success);
    }

    // ---------------------------------------------------------------------------------------------
    // Stash
    // ---------------------------------------------------------------------------------------------

    public async Task StashAsync(string message, bool includeUntracked)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(new[] { "-m", message.Trim() });
        await GitAsync(args, false);
        Info("Changes stashed - the working tree is clean.", LogLevel.Success);
    }

    public async Task ApplyStashAsync(string stash, bool pop)
    {
        await GitAsync(new[] { "stash", pop ? "pop" : "apply", stash }, false, "The stash didn't apply cleanly.",
            "Conflicted files are on the Changes tab; the stash is kept until it applies cleanly.");
    }

    public Task DropStashAsync(string stash) => GitAsync("stash", "drop", stash);

    // ---------------------------------------------------------------------------------------------
    // Worktrees
    // ---------------------------------------------------------------------------------------------

    /// <summary>Checks out <paramref name="branch"/> in a new folder. A branch that doesn't exist yet is created
    /// from <paramref name="startPoint"/> (without tracking it, like Start branch).</summary>
    public async Task AddWorktreeAsync(string folder, string branch, string startPoint)
    {
        if (Path.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new UserFacingException($"{folder} already exists and isn't empty.", "Pick a new folder for the worktree.");
        var args = await Git.RefExistsAsync(_path, "refs/heads/" + branch, _ct)
            ? new[] { "worktree", "add", folder, branch }
            : new[] { "worktree", "add", "--no-track", "-b", branch, folder, startPoint };
        await GitAsync(args, false, "Could not create the worktree.",
            "A branch can only be checked out in one worktree at a time - pick another branch, or remove the worktree that has it.");
        Info($"Worktree ready: {folder} on '{branch}'.", LogLevel.Success);
    }

    public async Task RemoveWorktreeAsync(string folder, bool force)
    {
        var args = new List<string> { "worktree", "remove" };
        if (force) args.Add("--force");
        args.Add(folder);
        await GitAsync(args, false, "Could not remove the worktree.",
            "It has uncommitted or untracked changes - commit or stash them in that worktree, or remove it anyway (they are lost).");
    }

    public Task PruneWorktreesAsync() => GitAsync("worktree", "prune", "--verbose");

    /// <summary>The SVN round trip for <paramref name="workingCopy"/>, logging to the same activity panel.</summary>
    public SvnWorkflow SvnFlow(string workingCopy, string branch) => new(workingCopy, branch, _log, _ct);

    // ---------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------

    public async Task CreateTagAsync(string name, string? message = null, string? hash = null)
    {
        var args = new List<string> { "tag" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.AddRange(new[] { "-a", name, "-m", message.Trim() });
        }
        else
        {
            args.Add(name);
        }
        if (!string.IsNullOrWhiteSpace(hash)) args.Add(hash.Trim());
        await GitAsync(args, false, $"Could not create tag '{name}'.");
        Info($"Created tag '{name}'.", LogLevel.Success);
    }

    public async Task DeleteTagAsync(string name)
    {
        await GitAsync(new[] { "tag", "-d", name }, false, $"Could not delete tag '{name}'.");
        Info($"Deleted local tag '{name}'.", LogLevel.Success);
    }

    public async Task PushTagAsync(string name)
    {
        var remote = await RemoteAsync();
        await GitAsync(new[] { "push", remote, $"refs/tags/{name}" }, network: true, $"Could not push tag '{name}'.");
        Info($"Pushed tag '{name}' to {remote}.", LogLevel.Success);
    }

    public async Task PushAllTagsAsync()
    {
        var remote = await RemoteAsync();
        await GitAsync(new[] { "push", remote, "--tags" }, network: true, "Could not push tags.");
        Info($"Pushed all tags to {remote}.", LogLevel.Success);
    }

    public async Task DeleteRemoteTagAsync(string name)
    {
        var remote = await RemoteAsync();
        await GitAsync(new[] { "push", remote, "--delete", $"refs/tags/{name}" }, network: true, $"Could not delete remote tag '{name}'.");
        Info($"Deleted tag '{name}' on {remote}.", LogLevel.Success);
    }

    // ---------------------------------------------------------------------------------------------
    // .gitignore
    // ---------------------------------------------------------------------------------------------

    public static async Task AddToGitIgnoreAsync(string repoPath, string rule)
    {
        var gitignore = Path.Combine(repoPath, ".gitignore");
        var line = rule.Trim();
        if (File.Exists(gitignore))
        {
            var content = await File.ReadAllTextAsync(gitignore);
            if (!content.EndsWith('\n')) line = "\n" + line;
            else line += "\n";
            await File.AppendAllTextAsync(gitignore, line, Encoding.UTF8);
        }
        else
        {
            await File.WriteAllTextAsync(gitignore, line + "\n", Encoding.UTF8);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Custom Git command runner
    // ---------------------------------------------------------------------------------------------

    /// <summary>Logged git call for small dialogs (remotes, submodules, gc); throws with git's message on failure.</summary>
    public Task<GitResult> RunGitAsync(IReadOnlyList<string> args, bool network = false) => GitAsync(args, network);

    public async Task<GitResult> RunCustomGitAsync(string commandLine)
    {
        var parts = ParseArguments(commandLine.Trim());
        if (parts.Count == 0) throw new UserFacingException("No command entered.");
        if (parts[0].Equals("git", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count == 0) throw new UserFacingException("No command entered.");

        Info("git " + string.Join(' ', parts.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), LogLevel.Command);
        var res = await Git.RunAsync(_path, parts, _ct, Git.NetworkTimeout);
        foreach (var line in res.CombinedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100))
        {
            Info("  " + line, res.Success ? LogLevel.Command : LogLevel.Warning);
        }
        if (!res.Success && res.ExitCode != 0)
        {
            Info($"git exited with code {res.ExitCode}", LogLevel.Error);
        }
        else
        {
            Info("Done.", LogLevel.Success);
        }
        return res;
    }

    /// <summary>
    /// Why a quick-bar command can throw work away (the caller asks before running it), or null when it can't.
    /// <paramref name="args"/> is what follows "git".
    /// </summary>
    public static string? DestructiveReason(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        bool Has(params string[] flags) => rest.Any(a => flags.Contains(a, StringComparer.Ordinal));
        // -f inside a short-flag cluster: -fd, -xdf
        bool HasShort(char c) => rest.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.Contains(c));

        return verb switch
        {
            "reset" when Has("--hard", "--merge", "--keep") => "reset --hard throws away uncommitted changes.",
            "clean" when HasShort('f') || Has("--force") => "clean -f deletes untracked files for good.",
            "push" when HasShort('f') || Has("--force", "--force-with-lease", "--force-if-includes", "--mirror")
                        || rest.Any(a => a.StartsWith("--force-with-lease=", StringComparison.Ordinal)) =>
                "A force push can replace commits on the server.",
            "push" when HasShort('d') || Has("--delete", "--prune") || rest.Any(a => a.StartsWith(':') || a.StartsWith('+')) =>
                "This push deletes or overwrites branches on the server.",
            "branch" when HasShort('D') || (Has("--delete", "-d") && Has("--force", "-f")) => "branch -D deletes a branch even if it isn't merged.",
            "checkout" when Has("--", ".") || HasShort('f') || Has("--force") => "checkout over files throws away their uncommitted changes.",
            "restore" when !Has("--staged", "-S") || Has("--worktree", "-W") => "restore throws away uncommitted changes to those files.",
            "stash" when rest.FirstOrDefault() is "drop" or "clear" => "Dropped stashes can't be brought back from the app.",
            "tag" when HasShort('d') || Has("--delete") => "This deletes a tag.",
            "rm" when !Has("--cached") => "git rm deletes the files from disk too.",
            "rebase" or "filter-branch" or "filter-repo" or "update-ref" or "reflog" or "gc" or "prune" =>
                $"git {verb} rewrites or removes history or files.",
            _ => null,
        };
    }

    public static List<string> ParseArguments(string commandLine)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Clone / identity
    // ---------------------------------------------------------------------------------------------

    public static async Task CloneAsync(string url, string target, IProgress<LogEntry> log, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        log.Report(new LogEntry($"git clone {url} \"{target}\"", LogLevel.Command));
        var r = await Git.RunAsync(parent, new[] { "clone", url, target }, ct, TimeSpan.FromMinutes(15));
        if (!r.Success) throw new UserFacingException($"Clone of {url} failed.", r.CombinedOutput);
        log.Report(new LogEntry($"Cloned into {target}.", LogLevel.Success));
    }

    public static async Task<(string Name, string Email)> GetIdentityAsync(string path)
    {
        var name = (await Git.RunAsync(path, new[] { "config", "user.name" })).StdOut.Trim();
        var email = (await Git.RunAsync(path, new[] { "config", "user.email" })).StdOut.Trim();
        return (name, email);
    }

    /// <summary>Sets user.name / user.email in this repo's own config, so its commits use them whatever the
    /// global identity is.</summary>
    public async Task SetLocalIdentityAsync(string name, string email)
    {
        if (name.Trim().Length > 0) await GitAsync("config", "--local", "user.name", name.Trim());
        if (email.Trim().Length > 0) await GitAsync("config", "--local", "user.email", email.Trim());
        Info($"This repo now commits as {name.Trim()} <{email.Trim()}>.", LogLevel.Success);
    }

    /// <summary>Removes the repo's own user.name / user.email, so the global identity applies again.</summary>
    public async Task ClearLocalIdentityAsync()
    {
        await Git.RunAsync(_path, new[] { "config", "--local", "--unset", "user.name" }, _ct);
        await Git.RunAsync(_path, new[] { "config", "--local", "--unset", "user.email" }, _ct);
        Info("This repo uses your global git identity again.", LogLevel.Success);
    }

    public static async Task SetGlobalIdentityAsync(string name, string email)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await Git.RunAsync(home, new[] { "config", "--global", "user.name", name });
        await Git.RunAsync(home, new[] { "config", "--global", "user.email", email });
    }
}
