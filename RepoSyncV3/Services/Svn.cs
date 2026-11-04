using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>Outcome of <see cref="Svn.MirrorCommitAsync"/>.</summary>
public sealed record SvnMirrorResult(bool Success, string? Error = null, bool Committed = false, bool WouldCommit = false,
    string Revision = "", int LocalChangesKept = 0, int FileCount = 0, string Message = "", string? Warning = null)
{
    public static SvnMirrorResult Fail(string error) => new(false, error);
}

/// <summary>
/// SVN-mirror mode (ported from git-sync-v2): a folder that is both an SVN working copy and a git repo, whose
/// git 'main' records only what SVN has committed (the working copy's BASE revision) - never local work.
/// svn runs non-interactively; a password it needs must already be saved (run any svn command once in a terminal).
/// </summary>
public static class Svn
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(30);

    /// <summary>svn status items whose working file differs from SVN's BASE: the mirror stages BASE instead.</summary>
    private static readonly HashSet<string> UseSvnBaseItems = new(StringComparer.Ordinal)
        { "modified", "conflicted", "deleted", "missing", "replaced", "merged", "obstructed", "incomplete" };

    /// <summary>svn status items not committed to SVN, so they must not be in the mirror.</summary>
    private static readonly HashSet<string> NotInSvnItems = new(StringComparer.Ordinal) { "added", "unversioned", "ignored" };

    /// <summary>git-only housekeeping files SVN never tracks.</summary>
    public static readonly HashSet<string> GitOnlyFiles = new(StringComparer.OrdinalIgnoreCase) { ".git", ".gitignore", ".gitattributes" };

    /// <summary>svn's E195017: 'svn cat' was given a directory.</summary>
    private const string SvnIsDirectoryError = "E195017";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsWorkingCopy(string path) => Directory.Exists(Path.Combine(path, ".svn"));

    /// <summary>The SVN working copy a card belongs to: the folder itself, or the main repo of a linked worktree.</summary>
    public static string? WorkingCopyFor(string repoPath, string? worktreeOf) =>
        IsWorkingCopy(repoPath) ? repoPath : worktreeOf is not null && IsWorkingCopy(worktreeOf) ? worktreeOf : null;

    public static Task<GitResult> RunAsync(string dir, IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RunProcessAsync("svn", dir, args, timeout ?? LocalTimeout, null, ct);

    /// <summary>git with extra environment (GIT_INDEX_FILE for the scratch index).</summary>
    private static Task<GitResult> GitEnvAsync(string dir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct) =>
        RunProcessAsync("git", dir, new[] { "-c", "core.quotepath=off" }.Concat(args).ToList(), null, env, ct);

    private static async Task<GitResult> RunProcessAsync(string exe, string dir, IReadOnlyList<string> args, TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linkedCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts?.Token ?? ct;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new GitResult { Success = false, ExitCode = -1, StdErr = $"Could not start {exe}: {ex.Message}. Is it on PATH?" };
        }
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderr = process.StandardError.ReadToEndAsync(effectiveCt);
            await process.WaitForExitAsync(effectiveCt);
            return new GitResult { Success = process.ExitCode == 0, ExitCode = process.ExitCode, StdOut = await stdout, StdErr = await stderr };
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            TryKill(process);
            return new GitResult { Success = false, ExitCode = -1, StdErr = $"{exe} timed out after {timeout!.Value.TotalMinutes:0} min." };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
    }

    /// <summary>A TortoiseSVN program (TortoiseProc.exe, TortoiseMerge.exe) on PATH or in the default install folder.</summary>
    public static string? FindTortoiseTool(string exe)
    {
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // a malformed PATH entry - fall back to the default install folder
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin", exe);
        return File.Exists(installed) ? installed : null;
    }

    public static async Task<string?> GetRevisionAsync(string wc, CancellationToken ct = default)
    {
        var info = await RunAsync(wc, new[] { "info", "--show-item", "revision" }, ct: ct);
        return info.Success ? info.StdOut.Trim() : null;
    }

    // ---------------------------------------------------------------------------------------------
    // Mirror commit
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Commits SVN's committed state (BASE) to the checked-out git branch, never local uncommitted work. Staging
    /// happens in a scratch copy of the index, so a dry run or a failure leaves the real index alone:
    ///   1. 'svn status --xml --no-ignore' lists every path whose working file is not SVN's BASE,
    ///   2. 'git add -A' stages the working tree (whatever 'svn update' brought in),
    ///   3. locally changed files get SVN's BASE content; added/unversioned/ignored paths are left out,
    ///   4. write-tree + commit-tree, the branch moves only if it still points where it did, then a mixed reset.
    /// The commit is tagged svn-rNNNN.
    /// </summary>
    public static async Task<SvnMirrorResult> MirrorCommitAsync(string wc, bool dryRun, CancellationToken ct = default)
    {
        var tempIndex = Path.Combine(Path.GetTempPath(), $"repo-sync-svn-mirror-{Guid.NewGuid():N}.index");
        var tempFiles = new List<string> { tempIndex };
        try
        {
            var svnStatus = await RunAsync(wc, new[] { "status", "--xml", "--no-ignore", "--ignore-externals" }, ct: ct);
            if (!svnStatus.Success) return SvnMirrorResult.Fail($"'svn status' failed: {svnStatus.CombinedOutput}");

            List<(string Path, string Item, bool TreeConflicted)> entries;
            try
            {
                entries = ParseStatusXml(svnStatus.StdOut);
            }
            catch (Exception ex)
            {
                return SvnMirrorResult.Fail($"Could not read 'svn status --xml' output: {ex.Message}");
            }

            var treeConflicts = entries.Where(e => e.TreeConflicted).Select(e => e.Path).ToList();
            if (treeConflicts.Count > 0) return SvnMirrorResult.Fail($"SVN tree conflict(s) - resolve them in SVN first: {string.Join(", ", treeConflicts)}");

            var revision = await GetRevisionLabelAsync(wc, ct);

            var realIndex = Path.Combine(wc, ".git", "index");
            if (File.Exists(realIndex)) File.Copy(realIndex, tempIndex, overwrite: true);
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };

            var add = await GitEnvAsync(wc, new[] { "add", "-A" }, env, ct);
            if (!add.Success) return SvnMirrorResult.Fail($"'git add' failed: {add.CombinedOutput}");

            var localChanges = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (GitOnlyFiles.Contains(entry.Path)) continue;

                if (NotInSvnItems.Contains(entry.Item))
                {
                    // an unversioned path git also ignores (e.g. a hardlinked asset folder) isn't real pending work
                    if (entry.Item == "added" || (entry.Item == "unversioned" && !await IsGitIgnoredAsync(wc, entry.Path, ct))) localChanges++;
                    var rm = await GitEnvAsync(wc, new[] { "--literal-pathspecs", "rm", "-r", "-q", "--cached", "--ignore-unmatch", "--", entry.Path }, env, ct);
                    if (!rm.Success) return SvnMirrorResult.Fail($"Could not leave '{entry.Path}' out of the mirror: {rm.CombinedOutput}");
                }
                else if (UseSvnBaseItems.Contains(entry.Item))
                {
                    localChanges++;
                    var baseFile = Path.Combine(Path.GetTempPath(), $"repo-sync-svn-base-{Guid.NewGuid():N}.tmp");
                    tempFiles.Add(baseFile);
                    var cat = await CatBaseToFileAsync(wc, entry.Path, baseFile, ct);
                    if (!cat.Success)
                    {
                        // a deleted directory has no content of its own; its files are listed one by one
                        if (entry.Item == "deleted" && cat.StdErr.Contains(SvnIsDirectoryError, StringComparison.Ordinal)) continue;
                        return SvnMirrorResult.Fail($"Could not read the SVN BASE of '{entry.Path}' ({entry.Item}): {cat.CombinedOutput}");
                    }
                    var stage = await StageFileContentAsync(wc, entry.Path, baseFile, env, ct);
                    if (!stage.Success) return SvnMirrorResult.Fail($"Could not stage the SVN BASE of '{entry.Path}': {stage.CombinedOutput}");
                }
            }

            var head = await Git.RunAsync(wc, new[] { "rev-parse", "--verify", "-q", "HEAD" }, ct);
            var headSha = head.Success ? head.StdOut.Trim() : null;

            var diffArgs = new List<string> { "diff", "--cached", "--name-status", "--no-renames" };
            if (headSha is not null) diffArgs.Add(headSha);
            var diff = await GitEnvAsync(wc, diffArgs, env, ct);
            if (!diff.Success) return SvnMirrorResult.Fail($"'git diff --cached' failed: {diff.CombinedOutput}");

            var changed = diff.StdOut.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => (Code: p[0][0], Path: p[1])).ToList();
            if (changed.Count == 0) return new SvnMirrorResult(true, Revision: revision, LocalChangesKept: localChanges);

            var message = $"SVN {revision} mirror: {BuildMessage(changed)}";
            if (dryRun) return new SvnMirrorResult(true, WouldCommit: true, Revision: revision, LocalChangesKept: localChanges, FileCount: changed.Count, Message: message);

            var tree = await GitEnvAsync(wc, new[] { "write-tree" }, env, ct);
            if (!tree.Success) return SvnMirrorResult.Fail($"'git write-tree' failed: {tree.CombinedOutput}");

            var messageFile = Path.Combine(Path.GetTempPath(), $"repo-sync-svn-msg-{Guid.NewGuid():N}.txt");
            tempFiles.Add(messageFile);
            await File.WriteAllTextAsync(messageFile, message, Utf8, ct);

            var commitArgs = new List<string> { "commit-tree", tree.StdOut.Trim(), "-F", messageFile };
            if (headSha is not null) commitArgs.AddRange(new[] { "-p", headSha });
            var commit = await Git.RunAsync(wc, commitArgs, ct);
            if (!commit.Success) return SvnMirrorResult.Fail($"'git commit-tree' failed: {commit.CombinedOutput}");
            var commitSha = commit.StdOut.Trim();

            // moves the checked-out branch only if it still points at headSha
            var updateArgs = new List<string> { "update-ref", "-m", $"repo-sync: SVN {revision} mirror", "HEAD", commitSha };
            if (headSha is not null) updateArgs.Add(headSha);
            var update = await Git.RunAsync(wc, updateArgs, ct);
            if (!update.Success) return SvnMirrorResult.Fail($"Could not move the branch to the mirror commit: {update.CombinedOutput}");

            // mixed reset: the real index matches the new commit; working files are not touched
            var reset = await Git.RunAsync(wc, new[] { "reset", "-q" }, ct);
            var warning = reset.Success ? null : $"Committed, but refreshing the git index failed ('git reset' - run it by hand): {reset.CombinedOutput}";
            warning ??= await TagRevisionAsync(wc, revision, commitSha, ct);

            return new SvnMirrorResult(true, Committed: true, Revision: revision, LocalChangesKept: localChanges, FileCount: changed.Count, Message: message, Warning: warning);
        }
        finally
        {
            foreach (var file in tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>"ADD: a, b; MODIFY: c" - or counts when more than 20 files.</summary>
    public static string BuildMessage(IReadOnlyList<(char Code, string Path)> changed)
    {
        var groups = new (char Code, string Verb, string Done)[] { ('A', "ADD", "ADDED"), ('M', "MODIFY", "MODIFIED"), ('D', "DELETE", "DELETED") }
            .Select(g => (g.Verb, g.Done, Files: changed.Where(c => (c.Code is 'A' or 'D' ? c.Code : 'M') == g.Code).Select(c => c.Path).ToList()))
            .Where(g => g.Files.Count > 0).ToList();
        return changed.Count > 20
            ? string.Join(", ", groups.Select(g => $"{g.Files.Count} {g.Done}"))
            : string.Join("; ", groups.Select(g => $"{g.Verb}: {string.Join(", ", g.Files)}"));
    }

    /// <summary>Tags a mirror commit svn-rNNNN. A mixed-revision label ("r5018:5019") is not tagged.</summary>
    private static async Task<string?> TagRevisionAsync(string wc, string revision, string commitSha, CancellationToken ct)
    {
        if (!Regex.IsMatch(revision, @"^r\d+$")) return null;
        var tag = $"svn-{revision}";
        var existing = await Git.RunAsync(wc, new[] { "tag", "--list", tag }, ct);
        if (existing.Success && !string.IsNullOrWhiteSpace(existing.StdOut)) return null;
        var create = await Git.RunAsync(wc, new[] { "tag", "-a", tag, "-m", $"SVN {revision}", commitSha }, ct);
        return create.Success ? null : $"Committed, but tagging it {tag} failed: {create.CombinedOutput}";
    }

    public static async Task<bool> IsGitIgnoredAsync(string dir, string path, CancellationToken ct)
    {
        // exit 0 = ignored; --no-index also checks paths git already tracks
        var result = await Git.RunAsync(dir, new[] { "check-ignore", "-q", "--no-index", "--", path }, ct);
        return result.ExitCode == 0;
    }

    public static List<(string Path, string Item, bool TreeConflicted)> ParseStatusXml(string xml)
    {
        var list = new List<(string, string, bool)>();
        foreach (var entry in XDocument.Parse(xml).Descendants("entry"))
        {
            var path = entry.Attribute("path")?.Value;
            var wcStatus = entry.Element("wc-status");
            var item = wcStatus?.Attribute("item")?.Value;
            if (string.IsNullOrEmpty(path) || path == "." || string.IsNullOrEmpty(item)) continue;
            list.Add((path.Replace('\\', '/'), item, wcStatus?.Attribute("tree-conflicted")?.Value == "true"));
        }
        return list;
    }

    /// <summary>"r5019", or "r5018:5019" for a mixed-revision working copy.</summary>
    private static async Task<string> GetRevisionLabelAsync(string wc, CancellationToken ct)
    {
        var version = await RunProcessAsync("svnversion", wc, new[] { "-n", "." }, LocalTimeout, null, ct);
        var text = version.StdOut.Trim().TrimEnd('M', 'S', 'P');
        if (version.Success && text.Length > 0 && char.IsDigit(text[0])) return $"r{text}";
        return await GetRevisionAsync(wc, ct) is { } r ? $"r{r}" : "r?";
    }

    /// <summary>Writes the exact bytes SVN has committed for <paramref name="path"/> (BASE) to a file.</summary>
    private static async Task<GitResult> CatBaseToFileAsync(string wc, string path, string outputFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "svn",
            WorkingDirectory = wc,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // the trailing '@' is an empty peg revision, so an '@' in the file name is read literally
        foreach (var a in new[] { "cat", "-r", "BASE", path + "@" }) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await using (var output = File.Create(outputFile))
            {
                await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
            }
            await process.WaitForExitAsync(ct);
            return new GitResult { Success = process.ExitCode == 0, ExitCode = process.ExitCode, StdErr = await stderrTask };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Puts a file's exact bytes into the (scratch) index as <paramref name="path"/>, keeping its file mode.</summary>
    private static async Task<GitResult> StageFileContentAsync(string wc, string path, string contentFile, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        // --no-filters: store the bytes as-is, exactly as SVN has them
        var hash = await Git.RunAsync(wc, new[] { "hash-object", "-w", "--no-filters", "--", contentFile }, ct);
        if (!hash.Success) return hash;
        var mode = "100644";
        var existing = await GitEnvAsync(wc, new[] { "--literal-pathspecs", "ls-files", "-s", "--", path }, env, ct);
        if (existing.Success && existing.StdOut.StartsWith("100755", StringComparison.Ordinal)) mode = "100755";
        return await GitEnvAsync(wc, new[] { "update-index", "--add", "--cacheinfo", mode, hash.StdOut.Trim(), path }, env, ct);
    }
}
