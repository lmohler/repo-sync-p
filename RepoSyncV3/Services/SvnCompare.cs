using System.Diagnostics;
using System.IO;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>One file that differs between SVN (git 'main') and the work folder.</summary>
/// <param name="Code">A = only in the work folder, D = only in SVN, M = in both but different.</param>
/// <param name="Uncommitted">Not committed on the work branch yet, so Send would not take it.</param>
public sealed record SvnDiffEntry(char Code, string Path, bool Uncommitted)
{
    public string StatusText => Code switch { 'A' => "New", 'D' => "Deleted", _ => "Modified" };
}

/// <summary>
/// Read-only comparison of the work folder with SVN (ported from git-sync-v2's Compare window). The SVN side is git
/// 'main' (what Send diffs against); the work side is the work folder's files on disk, so uncommitted edits and
/// untracked files are included and flagged. Nothing here changes git, SVN or any file outside the temp folder.
/// </summary>
public sealed class SvnCompare
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "repo-sync-svn-compare");

    private readonly string _wc;
    private readonly string _worktree;
    private string _mirror = "main";

    public SvnCompare(string svnWorkingCopy, string worktree)
    {
        _wc = svnWorkingCopy;
        _worktree = worktree;
    }

    private Task<GitResult> GitAsync(IReadOnlyList<string> args, CancellationToken ct, string? dir = null) => Git.RunAsync(dir ?? _worktree, args, ct);

    private static IEnumerable<string> NulFields(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"SVN r5039 (git 'main')" plus anything that makes 'main' differ from SVN itself.</summary>
    public async Task<(string Description, List<string> Warnings)> GetStateAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var branch = (await GitAsync(new[] { "branch", "--show-current" }, ct, _wc)).StdOut.Trim();
        if (branch is "main" or "master") _mirror = branch;
        else warnings.Add($"git in the SVN working copy is on '{branch}', not 'main' - switch it back: git -C \"{_wc}\" switch main");

        var tag = (await GitAsync(new[] { "describe", "--tags", "--match", "svn-r*", "--abbrev=0", _mirror }, ct)).StdOut.Trim();
        var mirrorRevision = tag.StartsWith("svn-r", StringComparison.Ordinal) ? tag[5..] : null;

        var wcRevision = await Svn.GetRevisionAsync(_wc, ct);
        if (wcRevision is null) warnings.Add("svn didn't answer (not on PATH?), so the SVN working copy itself was not checked.");
        else
        {
            if (mirrorRevision is not null && wcRevision != mirrorRevision && !await UnchangedSinceAsync(mirrorRevision, wcRevision, ct))
                warnings.Add($"The SVN working copy is at r{wcRevision} but git '{_mirror}' records r{mirrorRevision}: run Sync from SVN to bring '{_mirror}' up to date.");

            var status = await Svn.RunAsync(_wc, new[] { "status", "-q", "--ignore-externals" }, ct: ct);
            var local = status.Success ? status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
            if (local > 0)
                warnings.Add($"The SVN working copy has {local} local change(s) (a Send not committed yet?). Commit it in TortoiseSVN then Sync from SVN, or Undo last send.");
        }

        return (mirrorRevision is null ? $"SVN (git '{_mirror}')" : $"SVN r{mirrorRevision} (git '{_mirror}')", warnings);
    }

    /// <summary>SVN revisions are repo-wide: a commit elsewhere moves the working copy's revision without changing
    /// anything in it. True when nothing under the working copy changed after the mirror's revision.</summary>
    private async Task<bool> UnchangedSinceAsync(string mirrorRevision, string wcRevision, CancellationToken ct)
    {
        if (!long.TryParse(mirrorRevision, out var mirror) || !long.TryParse(wcRevision, out var wc) || wc < mirror) return false;
        var info = await Svn.RunAsync(_wc, new[] { "info", "--show-item", "last-changed-revision" }, ct: ct);
        return info.Success && long.TryParse(info.StdOut.Trim(), out var lastChanged) && lastChanged <= mirror;
    }

    /// <summary>Every file that differs between the mirror and the work folder (committed or not), sorted by path.</summary>
    public async Task<List<SvnDiffEntry>> GetChangesAsync(CancellationToken ct = default)
    {
        var diff = await GitAsync(new[] { "diff", "--name-status", "--no-renames", "-z", _mirror }, ct);
        if (!diff.Success) throw new UserFacingException("Could not compare the work folder with SVN.", diff.CombinedOutput);

        var uncommitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in NulFields((await GitAsync(new[] { "diff", "--name-only", "--no-renames", "-z", "HEAD" }, ct)).StdOut)) uncommitted.Add(path);
        var untracked = await GitAsync(new[] { "ls-files", "--others", "--exclude-standard", "-z" }, ct);

        var entries = new List<SvnDiffEntry>();
        var fields = NulFields(diff.StdOut).ToList();
        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            var code = fields[i][0] is 'A' or 'D' ? fields[i][0] : 'M';
            entries.Add(new SvnDiffEntry(code, fields[i + 1], uncommitted.Contains(fields[i + 1])));
        }
        var known = entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        entries.AddRange(NulFields(untracked.StdOut).Where(p => !known.Contains(p)).Select(p => new SvnDiffEntry('A', p, Uncommitted: true)));
        return entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The diff for one file, SVN side first.</summary>
    public async Task<List<DiffLine>> GetDiffAsync(SvnDiffEntry entry, CancellationToken ct = default)
    {
        var untracked = entry.Code == 'A' && entry.Uncommitted
            && !(await GitAsync(new[] { "ls-files", "--error-unmatch", "--", entry.Path }, ct)).Success;
        var args = new List<string> { "diff", "--no-renames", "--no-color" };
        if (untracked) args.AddRange(new[] { "--no-index", "--", "/dev/null", entry.Path });
        else args.AddRange(new[] { _mirror, "--", entry.Path });
        var result = await GitAsync(args, ct);
        var lines = Git.ParseDiff(result.StdOut);
        if (lines.Count == 0) lines.Add(new DiffLine { Text = "No difference in content (only line endings or file mode may differ).", Kind = DiffKind.Meta });
        return lines;
    }

    /// <summary>TortoiseMerge with SVN's version on the left and the work folder's file itself on the right
    /// (so edits there land in the work folder). Returns an error, or null when it opened.</summary>
    public async Task<string?> OpenInTortoiseMergeAsync(SvnDiffEntry entry, CancellationToken ct = default)
    {
        var merge = Svn.FindTortoiseTool("TortoiseMerge.exe");
        if (merge is null) return "TortoiseMerge.exe was not found (it comes with TortoiseSVN).";

        var folder = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(entry.Path);

        var left = Path.Combine(folder, "svn-" + name);
        if (entry.Code == 'A') await File.WriteAllBytesAsync(left, Array.Empty<byte>(), ct);
        else if (!await WriteBlobAsync($"{_mirror}:{entry.Path}", left, ct)) return $"Could not read {entry.Path} from git '{_mirror}'.";

        var right = Path.Combine(_worktree, entry.Path.Replace('/', '\\'));
        if (entry.Code == 'D')
        {
            right = Path.Combine(folder, "work-" + name);
            await File.WriteAllBytesAsync(right, Array.Empty<byte>(), ct);
        }

        // TortoiseSVN tools read /name:"value", so the line is built by hand
        var shown = entry.Path.Replace("\"", string.Empty);
        Process.Start(new ProcessStartInfo(merge, $"/base:\"{left}\" /mine:\"{right}\" /basename:\"SVN: {shown}\" /minename:\"Work folder: {shown}\"")
        { UseShellExecute = false });
        return null;
    }

    /// <summary>Writes a git blob byte for byte (binary files survive).</summary>
    private async Task<bool> WriteBlobAsync(string spec, string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _worktree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "cat-file", "blob", spec }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await using (var file = File.Create(target))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(file, ct);
        }
        await process.WaitForExitAsync(ct);
        await stderr;
        return process.ExitCode == 0;
    }

    /// <summary>Removes temp copies of earlier comparisons older than <paramref name="olderThan"/>, so a TortoiseMerge
    /// window that's still open keeps its files (best effort: one may still have older ones open).</summary>
    public static void CleanTemp(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                if (Directory.GetCreationTimeUtc(dir) > DateTime.UtcNow - olderThan) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* still open */ }
            }
        }
        catch
        {
            // best effort
        }
    }
}
