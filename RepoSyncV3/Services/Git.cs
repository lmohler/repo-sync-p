using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// Every git operation shells out to git.exe in the repo folder, so hooks, the credential manager and your
/// global config behave exactly as they do on the command line. Output is read as UTF-8 with
/// core.quotepath off, so file names come back as-is.
/// </summary>
public static class Git
{
    /// <summary>Fetch/push/clone get this long; a stalled connection shows up as an error instead of a hang.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

    private static readonly UTF8Encoding Utf8 = new(false);
    private const char Sep = '\x1f';

    /// <param name="onProgress">Called (on a background thread) with each progress line git writes to stderr. With a
    /// timeout, the timeout then counts from the last progress line, so a big transfer that keeps moving isn't cut off.</param>
    public static async Task<GitResult> RunAsync(string repoPath, IReadOnlyList<string> args, CancellationToken ct = default, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null, Action<string>? onProgress = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in new[] { "-c", "core.quotepath=off", "-c", "color.ui=never" }.Concat(args)) psi.ArgumentList.Add(a);
        // No console here: never wait on a terminal prompt or an editor. The Git Credential Manager's own
        // sign-in window still appears when a server needs a login.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        // Read-only commands (status) don't rewrite .git/index, which would wake RepoWatcher in a loop and can
        // collide with an editor's git running at the same moment.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

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
            return new GitResult { Success = false, ExitCode = -1, StdErr = $"Could not start git: {ex.Message}. Is Git for Windows installed?" };
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderrTask = onProgress is null
                ? process.StandardError.ReadToEndAsync(effectiveCt)
                : ReadProgressAsync(process.StandardError, line =>
                {
                    if (timeout.HasValue) timeoutCts?.CancelAfter(timeout.Value);
                    onProgress(line);
                }, effectiveCt);
            await process.WaitForExitAsync(effectiveCt);
            return new GitResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                StdOut = await stdoutTask,
                StdErr = await stderrTask,
            };
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            TryKill(process);
            return new GitResult
            {
                Success = false,
                ExitCode = -1,
                StdErr = $"Timed out after {timeout!.Value.TotalSeconds:0}s (a dead connection, VPN, or a sign-in window waiting for you).",
            };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Reads stderr as it comes, reporting each \r- or \n-ended line. Returns stderr with progress redraws
    /// collapsed to their last state, so the activity log isn't flooded with "Receiving objects: 1%..99%".</summary>
    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<string> report, CancellationToken ct)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1024];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                all.Append(c);
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0) report(line.ToString());
                    line.Clear();
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0) report(line.ToString());
        return string.Join('\n', all.ToString().Split('\n').Select(l =>
        {
            var t = l.TrimEnd('\r');
            var i = t.LastIndexOf('\r');
            return i >= 0 ? t[(i + 1)..] : t;
        }));
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------------------------------------
    // Repo layout
    // ---------------------------------------------------------------------------------------------

    public static bool IsRepo(string path) => Path.Exists(Path.Combine(path, ".git"));

    /// <summary>'.git' for a main repo, or where a worktree's '.git' file points.</summary>
    public static string? GitDir(string repoPath)
    {
        var dotGit = Path.Combine(repoPath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        try
        {
            if (!File.Exists(dotGit)) return null;
            var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
            var target = line["gitdir:".Length..].Trim();
            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The .git folder that holds refs and objects: the main repo's .git for a linked worktree,
    /// otherwise the same as <see cref="GitDir"/>.</summary>
    public static string? CommonDir(string repoPath)
    {
        if (GitDir(repoPath) is not { } d) return null;
        var common = Path.Combine(d, "commondir");
        if (!File.Exists(common)) return d;
        try { return Path.GetFullPath(Path.Combine(d, File.ReadAllText(common).Trim())); }
        catch { return d; }
    }

    /// <summary>For a linked worktree, the folder of the main working tree it belongs to; null otherwise.</summary>
    public static string? MainWorktreePath(string repoPath)
    {
        if (GitDir(repoPath) is not { } d || CommonDir(repoPath) is not { } c) return null;
        if (string.Equals(Path.GetFullPath(d).TrimEnd('\\'), c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(Path.GetFileName(c.TrimEnd('\\')), ".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(c.TrimEnd('\\')) : null;
    }

    public static bool IsMergeInProgress(string repoPath) =>
        GitDir(repoPath) is { } d && (File.Exists(Path.Combine(d, "MERGE_HEAD")) || File.Exists(Path.Combine(d, "CHERRY_PICK_HEAD")) || File.Exists(Path.Combine(d, "REVERT_HEAD")));

    public static bool IsRebaseInProgress(string repoPath) =>
        GitDir(repoPath) is { } d && (Directory.Exists(Path.Combine(d, "rebase-merge")) || Directory.Exists(Path.Combine(d, "rebase-apply")));

    /// <summary>When the repo last talked to its remote (FETCH_HEAD is rewritten by every fetch/pull).</summary>
    public static DateTime? LastFetch(string repoPath)
    {
        // a worktree shares FETCH_HEAD with its main repo's .git
        if (CommonDir(repoPath) is not { } d) return null;
        var file = Path.Combine(d, "FETCH_HEAD");
        return File.Exists(file) ? File.GetLastWriteTime(file) : null;
    }

    /// <summary>Folders under <paramref name="root"/> (up to <paramref name="depth"/> levels) that are git repos.
    /// Doesn't look inside a repo it found.</summary>
    public static List<string> FindRepos(string root, int depth = 4)
    {
        var found = new List<string>();
        void Walk(string dir, int level)
        {
            if (IsRepo(dir))
            {
                found.Add(dir);
                return;
            }
            if (level >= depth) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "packages") continue;
                Walk(sub, level + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    // ---------------------------------------------------------------------------------------------
    // Remote
    // ---------------------------------------------------------------------------------------------

    /// <summary>'origin', or the repo's first remote when there is no origin. Null when it has none.</summary>
    public static async Task<string?> GetRemoteNameAsync(string path, CancellationToken ct = default)
    {
        var names = Lines((await RunAsync(path, new[] { "remote" }, ct)).StdOut).Select(n => n.Trim()).ToList();
        return names.Contains("origin") ? "origin" : names.FirstOrDefault();
    }

    /// <summary>The user.name / user.email a commit here would get (the most specific config wins), and whether
    /// the repo sets its own email.</summary>
    public static async Task<(string Name, string Email, bool LocalEmail)> GetIdentityAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "config", "--show-scope", "--get-regexp", @"^user\.(name|email)$" }, ct);
        string name = string.Empty, email = string.Empty;
        var local = false;
        // lines come system, global, local, worktree: later ones override earlier ones
        foreach (var line in Lines(r.StdOut))
        {
            var parts = line.Split(new[] { '\t', ' ' }, 3);
            if (parts.Length < 3) continue;
            if (parts[1] == "user.name") name = parts[2].Trim();
            if (parts[1] == "user.email")
            {
                email = parts[2].Trim();
                local = parts[0] is "local" or "worktree";
            }
        }
        return (name, email, local);
    }

    public static async Task<string?> GetRemoteUrlAsync(string path, string remote, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "remote", "get-url", remote }, ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    /// <summary>The remote's default branch (e.g. "main") from refs/remotes/&lt;remote&gt;/HEAD; when git doesn't know it
    /// yet it asks the server once, then falls back to main / master.</summary>
    public static async Task<string> GetDefaultBranchAsync(string path, string remote, CancellationToken ct = default)
    {
        if (await ReadRemoteHeadAsync(path, remote, ct) is { } known) return known;

        await RunAsync(path, new[] { "remote", "set-head", remote, "--auto" }, ct, NetworkTimeout);
        if (await ReadRemoteHeadAsync(path, remote, ct) is { } asked) return asked;

        foreach (var guess in new[] { "main", "master" })
        {
            if (await RefExistsAsync(path, $"refs/remotes/{remote}/{guess}", ct)) return guess;
        }
        return "main";
    }

    private static async Task<string?> ReadRemoteHeadAsync(string path, string remote, CancellationToken ct)
    {
        var head = await RunAsync(path, new[] { "symbolic-ref", "--short", $"refs/remotes/{remote}/HEAD" }, ct);
        var text = head.StdOut.Trim();
        return head.Success && text.StartsWith(remote + "/", StringComparison.Ordinal) ? text[(remote.Length + 1)..] : null;
    }

    public static async Task<bool> RefExistsAsync(string path, string reference, CancellationToken ct = default) =>
        (await RunAsync(path, new[] { "rev-parse", "--verify", "-q", reference }, ct)).Success;

    public static async Task<bool> IsValidBranchNameAsync(string path, string name, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(name) && (await RunAsync(path, new[] { "check-ref-format", "--branch", name }, ct)).Success;

    // ---------------------------------------------------------------------------------------------
    // Status
    // ---------------------------------------------------------------------------------------------

    private static readonly Regex BranchLineRegex =
        new(@"^(?<b>.+?)(?:\.\.\.(?<u>\S+))?(?: \[(?<ab>[^\]]+)\])?$", RegexOptions.Compiled);

    private static readonly HashSet<string> ConflictCodes = new() { "DD", "AU", "UD", "UA", "DU", "AA", "UU" };

    public static async Task<RepoStatus> GetStatusAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "status", "--porcelain=v1", "--branch", "--untracked-files=all" }, ct);
        if (!r.Success) return new RepoStatus { Ok = false, Error = r.CombinedOutput };

        var status = new RepoStatus { Ok = true };
        foreach (var line in Lines(r.StdOut))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchLine(line[3..], status);
                continue;
            }
            if (line.Length < 4) continue;

            var xy = line[..2];
            var x = line[0];
            var y = line[1];
            var file = line[3..];
            string? orig = null;
            if ((x is 'R' or 'C') && file.Contains(" -> "))
            {
                var parts = file.Split(" -> ", 2);
                orig = Unquote(parts[0]);
                file = parts[1];
            }
            file = Unquote(file);

            if (xy == "!!") continue;
            if (xy == "??")
            {
                status.Unstaged.Add(new FileChange { Path = file, Code = '?' });
                continue;
            }
            if (ConflictCodes.Contains(xy))
            {
                status.Conflicted.Add(new FileChange { Path = file, Code = '!' });
                continue;
            }
            if (x != ' ') status.Staged.Add(new FileChange { Path = file, OrigPath = orig, Code = x, Staged = true });
            if (y != ' ') status.Unstaged.Add(new FileChange { Path = file, Code = y });
        }
        return status;
    }

    private static void ParseBranchLine(string text, RepoStatus status)
    {
        foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                status.Branch = text[prefix.Length..];
                return;
            }
        }
        if (text.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
        {
            status.Branch = "detached HEAD";
            status.Detached = true;
            return;
        }

        var m = BranchLineRegex.Match(text);
        if (!m.Success)
        {
            status.Branch = text;
            return;
        }
        status.Branch = m.Groups["b"].Value;
        if (m.Groups["u"].Success) status.Upstream = m.Groups["u"].Value;
        var ab = m.Groups["ab"].Value;
        status.UpstreamGone = ab.Contains("gone");
        var ahead = Regex.Match(ab, @"ahead (\d+)");
        if (ahead.Success) status.Ahead = int.Parse(ahead.Groups[1].Value);
        var behind = Regex.Match(ab, @"behind (\d+)");
        if (behind.Success) status.Behind = int.Parse(behind.Groups[1].Value);
    }

    /// <summary>Porcelain quotes names with unusual characters in C style ("a\"b"); undo the common escapes.</summary>
    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        return s[1..^1].Replace("\\\"", "\"").Replace("\\t", "\t").Replace("\\\\", "\\");
    }

    /// <summary>Commits HEAD has that <paramref name="other"/> doesn't (ahead), and the reverse (behind).</summary>
    public static async Task<(int Ahead, int Behind)?> AheadBehindAsync(string path, string other, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "rev-list", "--left-right", "--count", $"HEAD...{other}" }, ct);
        var parts = r.StdOut.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        return r.Success && parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? (a, b) : null;
    }

    public static async Task<int> CountAsync(string path, string range, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "rev-list", "--count", range }, ct);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    public static async Task<CommitInfo?> GetHeadCommitAsync(string path, CancellationToken ct = default) =>
        (await GetLogAsync(path, "HEAD", 1, ct)).FirstOrDefault();

    // ---------------------------------------------------------------------------------------------
    // History
    // ---------------------------------------------------------------------------------------------

    /// <param name="search">Extra log args for a deep search (e.g. "-S", text or "--grep=..."); paths go after "--".</param>
    public static async Task<List<CommitInfo>> GetLogAsync(string path, string reference, int max, CancellationToken ct = default,
        IEnumerable<string>? search = null, IEnumerable<string>? paths = null)
    {
        var args = new List<string> { "log", $"-n{max}", "--date-order", $"--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%P%x1f%s%x1e" };
        if (search is not null) args.AddRange(search);
        args.Add(reference);
        args.Add("--");
        if (paths is not null) args.AddRange(paths);
        var r = await RunAsync(path, args, ct);
        var list = new List<CommitInfo>();
        if (!r.Success) return list;
        foreach (var record in r.StdOut.Split('\x1e'))
        {
            var f = record.Trim('\r', '\n').Split(Sep);
            if (f.Length < 8) continue;
            list.Add(new CommitInfo
            {
                Hash = f[0],
                ShortHash = f[1],
                Author = f[2],
                Email = f[3],
                Date = DateTime.TryParse(f[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue,
                Refs = f[5].Replace("HEAD -> ", string.Empty),
                IsMerge = f[6].Contains(' '),
                Parents = f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Subject = f[7],
            });
        }
        return list;
    }

    public static async Task<HashSet<string>> GetUnpushedHashesAsync(string path, string upstream, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "rev-list", $"{upstream}..HEAD" }, ct);
        return r.Success ? Lines(r.StdOut).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
    }

    public static async Task<List<CommitFile>> GetCommitFilesAsync(string path, string hash, CancellationToken ct = default)
    {
        // -m --first-parent: a merge commit lists what it brought in compared with the branch it was made on
        var r = await RunAsync(path, new[] { "show", "--format=", "--name-status", "-M", "-m", "--first-parent", hash }, ct);
        var list = new List<CommitFile>();
        foreach (var line in Lines(r.StdOut))
        {
            var f = line.Split('\t');
            if (f.Length < 2 || f[0].Length == 0) continue;
            list.Add(new CommitFile { Code = f[0][0], Path = f[^1] });
        }
        return list;
    }

    public static async Task<string> GetCommitBodyAsync(string path, string hash, CancellationToken ct = default) =>
        (await RunAsync(path, new[] { "show", "-s", "--format=%B", hash }, ct)).StdOut.Trim();

    // ---------------------------------------------------------------------------------------------
    // Diffs
    // ---------------------------------------------------------------------------------------------

    public const int MaxDiffLines = 6000;

    public static async Task<List<DiffLine>> GetFileDiffAsync(string path, FileChange change, bool ignoreWhitespace = false, CancellationToken ct = default)
    {
        if (change.IsUntracked) return NewFileDiff(Path.Combine(path, change.Path));
        var args = new List<string> { "diff", "-M" };
        if (ignoreWhitespace) args.Add("-w");
        if (change.Staged) args.Add("--cached");
        args.Add("--");
        if (change.OrigPath is not null) args.Add(change.OrigPath);
        args.Add(change.Path);
        return ParseDiff((await RunAsync(path, args, ct)).StdOut);
    }

    /// <summary>The unparsed diff of a tracked file (no -w), the input for <see cref="PartialPatch"/>.</summary>
    public static async Task<string> GetRawFileDiffAsync(string path, FileChange change, CancellationToken ct = default)
    {
        var args = new List<string> { "diff" };
        if (change.Staged) args.Add("--cached");
        args.Add("--");
        args.Add(change.Path);
        return (await RunAsync(path, args, ct)).StdOut;
    }

    public static async Task<List<DiffLine>> GetCommitDiffAsync(string path, string hash, string? file, bool ignoreWhitespace = false, CancellationToken ct = default)
    {
        var args = new List<string> { "show", "--format=", "-M", "-m", "--first-parent" };
        if (ignoreWhitespace) args.Add("-w");
        args.Add(hash);
        if (file is not null) args.AddRange(new[] { "--", file });
        return ParseDiff((await RunAsync(path, args, ct)).StdOut);
    }

    public static async Task<List<DiffLine>> GetStashDiffAsync(string path, string stash, bool ignoreWhitespace = false, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "show", "-p", "--include-untracked" };
        if (ignoreWhitespace) args.Add("-w");
        args.Add(stash);
        return ParseDiff((await RunAsync(path, args, ct)).StdOut);
    }

    private static List<DiffLine> NewFileDiff(string fullPath)
    {
        var lines = new List<DiffLine> { new() { Text = $"new file: {Path.GetFileName(fullPath)}", Kind = DiffKind.Meta } };
        try
        {
            if (Directory.Exists(fullPath))
            {
                lines.Add(new DiffLine { Text = "(folder)", Kind = DiffKind.Meta });
                return lines;
            }
            var info = new FileInfo(fullPath);
            if (info.Length > 5_000_000)
            {
                lines.Add(new DiffLine { Text = $"(large file, {info.Length / 1024 / 1024} MB - not shown)", Kind = DiffKind.Meta });
                return lines;
            }
            var text = File.ReadAllText(fullPath);
            if (text.Contains('\0'))
            {
                lines.Add(new DiffLine { Text = "(binary file)", Kind = DiffKind.Meta });
                return lines;
            }
            var n = 0;
            foreach (var l in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (++n > MaxDiffLines) break;
                lines.Add(new DiffLine { Text = "+" + l, Kind = DiffKind.Add, NewNo = n.ToString(), Ext = Path.GetExtension(fullPath).ToLowerInvariant() });
            }
        }
        catch (Exception ex)
        {
            lines.Add(new DiffLine { Text = $"(can't read the file: {ex.Message})", Kind = DiffKind.Meta });
        }
        return lines;
    }

    private static readonly Regex HunkRegex = new(@"^@@+ -(?<o>\d+)(?:,\d+)? \+(?<n>\d+)", RegexOptions.Compiled);

    public static List<DiffLine> ParseDiff(string text)
    {
        var result = new List<DiffLine>();
        int oldNo = 0, newNo = 0, hunk = -1, index = 0;
        var inHeader = true;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (result.Count >= MaxDiffLines)
            {
                result.Add(new DiffLine { Text = $"(diff cut off after {MaxDiffLines} lines)", Kind = DiffKind.Meta });
                break;
            }
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff ", StringComparison.Ordinal))
            {
                inHeader = true;
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Meta });
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHeader = false;
                var m = HunkRegex.Match(line);
                if (m.Success)
                {
                    oldNo = int.Parse(m.Groups["o"].Value);
                    newNo = int.Parse(m.Groups["n"].Value);
                }
                hunk++;
                index = 0;
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Hunk, Hunk = hunk });
                continue;
            }
            if (inHeader)
            {
                if (line.Length > 0) result.Add(new DiffLine { Text = line, Kind = DiffKind.Meta });
                continue;
            }
            if (line.StartsWith('+'))
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Add, NewNo = (newNo++).ToString(), Hunk = hunk, Index = index++ });
            else if (line.StartsWith('-'))
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Delete, OldNo = (oldNo++).ToString(), Hunk = hunk, Index = index++ });
            else if (line.StartsWith('\\'))
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Meta, Hunk = hunk, Index = index++ });
            else if (line.Length > 0 || oldNo > 0)
                result.Add(new DiffLine { Text = line, Kind = DiffKind.Context, OldNo = (oldNo++).ToString(), NewNo = (newNo++).ToString(), Hunk = hunk, Index = index++ });
        }
        // the split leaves one empty context line at the very end
        while (result.Count > 0 && result[^1].Kind == DiffKind.Context && result[^1].Text.Length == 0) result.RemoveAt(result.Count - 1);
        DiffDecor.Mark(result);
        return result;
    }

    public static async Task<List<ReflogEntry>> GetReflogAsync(string path, int max = 150, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "reflog", "-n", max.ToString(), "--format=%H%x1f%gd%x1f%gs%x1f%cr" }, ct);
        var list = new List<ReflogEntry>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\x1f');
            if (p.Length < 4) continue;
            list.Add(new ReflogEntry { Hash = p[0], Selector = p[1], Action = p[2], When = p[3] });
        }
        return list;
    }

    /// <summary>Files in HEAD at least minBytes big; with range ("a..b") only files that range touched.</summary>
    public static async Task<List<(string Path, long Bytes)>> GetLargeFilesAsync(string path, long minBytes, string? range = null, CancellationToken ct = default)
    {
        var args = new List<string> { "ls-tree", "-r", "-l", "HEAD" };
        if (range is not null)
        {
            var changed = await RunAsync(path, new[] { "diff", "--name-only", "--diff-filter=AM", range }, ct);
            var files = Lines(changed.StdOut).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (files.Count == 0) return new();
            args.Add("--");
            args.AddRange(files.Take(500));
        }
        var r = await RunAsync(path, args, ct);
        var list = new List<(string, long)>();
        foreach (var line in Lines(r.StdOut))
        {
            // <mode> blob <hash> <size>\t<path>
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length >= 4 && long.TryParse(meta[3], out var size) && size >= minBytes) list.Add((line[(tab + 1)..], size));
        }
        return list.OrderByDescending(f => f.Item2).ToList();
    }

    private static readonly HashSet<string> NotTicketWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "release", "hotfix", "version", "build", "rc", "v", "sprint", "phase", "step", "part", "temp", "wip", "test", "backup",
    };

    /// <summary>"PROJ-123" from "feature/PROJ-123-fix-login", or null.</summary>
    public static string? TicketFromBranch(string? branch)
    {
        if (string.IsNullOrEmpty(branch)) return null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(branch, @"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]+)-(\d+)"))
        {
            // release-2024, hotfix-3, v-2 are versions, not tickets
            if (NotTicketWords.Contains(m.Groups[1].Value)) continue;
            return $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}";
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Branches
    // ---------------------------------------------------------------------------------------------

    public static async Task<List<BranchInfo>> GetLocalBranchesAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[]
        {
            "for-each-ref", "refs/heads", "--sort=-committerdate",
            "--format=%(HEAD)%1f%(refname:short)%1f%(upstream:short)%1f%(upstream:track,nobracket)%1f%(committerdate:iso-strict)%1f%(subject)%1f%(authorname)",
        }, ct);
        var list = new List<BranchInfo>();
        foreach (var line in Lines(r.StdOut))
        {
            var f = line.Split(Sep);
            if (f.Length < 7) continue;
            var track = f[3];
            var ahead = Regex.Match(track, @"ahead (\d+)");
            var behind = Regex.Match(track, @"behind (\d+)");
            list.Add(new BranchInfo
            {
                IsCurrent = f[0] == "*",
                Name = f[1],
                Upstream = f[2].Length > 0 ? f[2] : null,
                UpstreamGone = track.Contains("gone"),
                Ahead = ahead.Success ? int.Parse(ahead.Groups[1].Value) : 0,
                Behind = behind.Success ? int.Parse(behind.Groups[1].Value) : 0,
                Date = DateTime.TryParse(f[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Subject = f[5],
                Author = f[6],
                HasLocal = true,
            });
        }
        return list;
    }

    public static async Task<List<BranchInfo>> GetRemoteBranchesAsync(string path, string remote, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[]
        {
            "for-each-ref", $"refs/remotes/{remote}", "--sort=-committerdate",
            "--format=%(refname:short)%1f%(committerdate:iso-strict)%1f%(subject)%1f%(authorname)%1f%(symref)",
        }, ct);
        var list = new List<BranchInfo>();
        foreach (var line in Lines(r.StdOut))
        {
            var f = line.Split(Sep);
            if (f.Length < 5 || f[4].Length > 0 || !f[0].Contains('/')) continue; // skip origin/HEAD
            list.Add(new BranchInfo
            {
                Name = f[0],
                Date = DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Subject = f[2],
                Author = f[3],
            });
        }
        return list;
    }

    public static async Task<HashSet<string>> GetMergedBranchesAsync(string path, string into, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "branch", "--format=%(refname:short)", "--merged", into }, ct);
        return r.Success ? Lines(r.StdOut).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
    }

    // ---------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------

    public static async Task<List<TagInfo>> GetTagsAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[]
        {
            "for-each-ref", "refs/tags", "--sort=-creatordate",
            "--format=%(refname:short)%1f%(objectname:short)%1f%(creatordate:iso-strict)%1f%(subject)%1f%(*objectname:short)",
        }, ct);
        var list = new List<TagInfo>();
        if (!r.Success) return list;
        foreach (var line in Lines(r.StdOut))
        {
            var f = line.Split(Sep);
            if (f.Length < 4) continue;
            var hash = f.Length >= 5 && f[4].Length > 0 ? f[4] : f[1];
            list.Add(new TagInfo
            {
                Name = f[0],
                Hash = hash,
                Date = DateTime.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Subject = f[3],
            });
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // File history & blame
    // ---------------------------------------------------------------------------------------------

    public static async Task<List<CommitInfo>> GetFileLogAsync(string path, string file, int max = 100, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[]
        {
            "log", $"-n{max}", "--date-order", $"--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%P%x1f%s%x1e", "--follow", "--", file,
        }, ct);
        var list = new List<CommitInfo>();
        if (!r.Success) return list;
        foreach (var record in r.StdOut.Split('\x1e'))
        {
            var f = record.Trim('\r', '\n').Split(Sep);
            if (f.Length < 8) continue;
            list.Add(new CommitInfo
            {
                Hash = f[0],
                ShortHash = f[1],
                Author = f[2],
                Email = f[3],
                Date = DateTime.TryParse(f[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue,
                Refs = f[5].Replace("HEAD -> ", string.Empty),
                IsMerge = f[6].Contains(' '),
                Parents = f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Subject = f[7],
            });
        }
        return list;
    }

    private static readonly Regex BlameRegex = new(@"^(?<hash>[0-9a-fA-F\^]+)\s+\((?<author>.+?)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<line>\d+)\)\s?(?<code>.*)$", RegexOptions.Compiled);

    public static async Task<List<BlameLine>> GetBlameAsync(string path, string file, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "blame", "-w", "--date=short", "--", file }, ct);
        var list = new List<BlameLine>();
        if (!r.Success) return list;
        foreach (var line in Lines(r.StdOut))
        {
            var m = BlameRegex.Match(line);
            if (m.Success)
            {
                list.Add(new BlameLine
                {
                    CommitHash = m.Groups["hash"].Value,
                    Author = m.Groups["author"].Value.Trim(),
                    Date = m.Groups["date"].Value,
                    LineNo = int.TryParse(m.Groups["line"].Value, out var n) ? n : 0,
                    Code = m.Groups["code"].Value,
                });
            }
            else
            {
                list.Add(new BlameLine { Code = line });
            }
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Stashes
    // ---------------------------------------------------------------------------------------------

    public static async Task<List<StashInfo>> GetStashesAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "stash", "list", "--format=%gd%x1f%cI%x1f%gs" }, ct);
        var list = new List<StashInfo>();
        foreach (var line in Lines(r.StdOut))
        {
            var f = line.Split(Sep);
            if (f.Length < 3) continue;
            list.Add(new StashInfo
            {
                Name = f[0],
                Date = DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                Message = f[2],
            });
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Worktrees
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every working tree of the repo (main first), from 'git worktree list --porcelain'.</summary>
    public static async Task<List<WorktreeInfo>> GetWorktreesAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(path, new[] { "worktree", "list", "--porcelain" }, ct);
        var list = new List<WorktreeInfo>();
        if (!r.Success) return list;
        var here = Path.GetFullPath(path).TrimEnd('\\');
        WorktreeInfo? cur = null;
        foreach (var raw in Lines(r.StdOut))
        {
            var line = raw.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var wt = Path.GetFullPath(line[9..].Replace('/', '\\')).TrimEnd('\\');
                cur = new WorktreeInfo
                {
                    Path = wt,
                    IsMain = list.Count == 0,
                    IsCurrent = string.Equals(wt, here, StringComparison.OrdinalIgnoreCase),
                };
                list.Add(cur);
            }
            else if (cur is null) continue;
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) cur.Head = line[5..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) cur.Branch = line[7..].Replace("refs/heads/", string.Empty);
            else if (line == "detached") cur.Detached = true;
            else if (line == "bare") cur.Bare = true;
            else if (line.StartsWith("locked", StringComparison.Ordinal)) cur.Locked = true;
            else if (line.StartsWith("prunable", StringComparison.Ordinal)) cur.Prunable = true;
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Commit message suggestion
    // ---------------------------------------------------------------------------------------------

    /// <summary>"Update a.cs, b.cs" / "Add x.cs; update a.cs" style summary of the files being committed.</summary>
    public static string SuggestMessage(IEnumerable<FileChange> changes)
    {
        var groups = changes
            .GroupBy(c => c.Code switch
            {
                'A' or '?' => "Add",
                'D' => "Delete",
                'R' => "Rename",
                _ => "Update",
            })
            .OrderBy(g => g.Key switch { "Add" => 0, "Update" => 1, "Rename" => 2, _ => 3 })
            .Select(g =>
            {
                var names = g.Select(c => c.FileName).Distinct().ToList();
                var shown = string.Join(", ", names.Take(4));
                if (names.Count > 4) shown += $" and {names.Count - 4} more";
                return $"{g.Key} {shown}";
            })
            .ToList();
        if (groups.Count == 0) return string.Empty;
        var text = string.Join("; ", groups);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
