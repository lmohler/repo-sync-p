using System.Text;
using System.Text.RegularExpressions;

namespace RepoSyncV3.Services;

/// <summary>Cuts a one-file unified diff down to the chosen +/- lines so <c>git apply --recount</c> can stage,
/// unstage or discard just those lines. Selection is (hunk, index) as numbered by <see cref="Git.ParseDiff"/>.
/// Handles CRLF files (the CRs are kept so the patch matches), new / deleted files (a partial patch of one
/// becomes a plain modification) and "\ No newline at end of file".</summary>
public static partial class PartialPatch
{
    /// <param name="rawDiff">Output of <c>git diff [--cached] -- file</c> (or <see cref="RawNewFileDiffAsync"/>).</param>
    /// <param name="selected">The chosen lines; lines that aren't +/- are ignored.</param>
    /// <param name="reverse">True when the patch will be applied with -R: unstage (from a --cached diff) and
    /// discard (from the worktree diff). Unpicked + lines then stay as context, unpicked - lines go.</param>
    /// <returns>The patch text, or null when nothing selected changes anything.</returns>
    public static string? Build(string rawDiff, IReadOnlySet<(int Hunk, int Index)> selected, bool reverse)
    {
        // split on \n only: a CRLF file's lines end in \r and must keep it or git apply won't find them
        var lines = rawDiff.Split('\n');
        var header = new List<string>();
        var hunks = new List<(string Head, List<string> Body)>();
        int hunk = -1, i = 0;
        var inHunks = false;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunks = true;
                hunk++;
                var hunkLines = new List<string>();
                var changed = false;
                var keptPrevious = true;
                var index = 0;
                i++;
                for (; i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !lines[i].StartsWith("diff ", StringComparison.Ordinal); i++, index++)
                {
                    var l = lines[i];
                    if (l.Length == 0 && i == lines.Length - 1) break;
                    var pick = selected.Contains((hunk, index));
                    switch (l.Length > 0 ? l[0] : ' ')
                    {
                        case '+':
                            if (pick) { hunkLines.Add(l); changed = true; keptPrevious = true; }
                            else if (reverse) { hunkLines.Add(" " + l[1..]); keptPrevious = true; }
                            else keptPrevious = false;
                            break;
                        case '-':
                            if (pick) { hunkLines.Add(l); changed = true; keptPrevious = true; }
                            else if (!reverse) { hunkLines.Add(" " + l[1..]); keptPrevious = true; }
                            else keptPrevious = false;
                            break;
                        case '\\':
                            if (keptPrevious) hunkLines.Add(l);
                            break;
                        default:
                            hunkLines.Add(l.Length == 0 ? " " : l);
                            keptPrevious = true;
                            break;
                    }
                }
                if (changed) hunks.Add((line, FixEndOfFile(hunkLines)));
                continue;
            }
            if (!inHunks && line.Length > 0) header.Add(line);
            i++;
        }
        if (hunks.Count == 0) return null;

        // A new file whose patch keeps some lines as context isn't a creation any more (the same for a deletion
        // that keeps lines): turn the header into a plain modification so git apply checks the right content.
        var oldSide = hunks.Any(h => h.Body.Any(l => l[0] is ' ' or '-'));
        var newSide = hunks.Any(h => h.Body.Any(l => l[0] is ' ' or '+'));
        var isNew = header.Any(h => h.StartsWith("new file mode", StringComparison.Ordinal));
        var isDeleted = header.Any(h => h.StartsWith("deleted file mode", StringComparison.Ordinal));
        if ((isNew && oldSide) || (isDeleted && newSide)) header = AsModification(header);

        var sb = new StringBuilder();
        foreach (var h in header) sb.Append(h).Append('\n');
        foreach (var (head, body) in hunks)
        {
            sb.Append(Rehead(head, body)).Append('\n');
            foreach (var b in body) sb.Append(b).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Discarding lines = reverse-applying the worktree diff, so the patch's new side must match the file
    /// as it is: unpicked + lines stay as context (the same shape as unstaging).</summary>
    public static string? ForDiscard(string rawDiff, IReadOnlySet<(int Hunk, int Index)> selected) => Build(rawDiff, selected, reverse: true);

    /// <summary>The diff of an untracked file against nothing (<c>git diff --no-index /dev/null file</c>): one hunk
    /// whose line N is index N-1, so lines picked in the "new file" view (by NewNo) can be staged one by one.</summary>
    public static async Task<string> RawNewFileDiffAsync(string repo, string file, CancellationToken ct = default) =>
        (await Git.RunAsync(repo, new[] { "diff", "--no-index", "--no-color", "--", "/dev/null", file.Replace('\\', '/') }, ct)).StdOut;

    /// <summary>
    /// "\ No newline at end of file" belongs to the line before it, and is only valid on the last line of its side.
    /// After cutting lines out, a marked line can end up with more lines after it on that side:
    /// a marked - / + line simply gets its newline back (drop the marker); a marked context line that now has
    /// lines added after it is split into -line (no newline) / +line (with newline).
    /// </summary>
    private static List<string> FixEndOfFile(List<string> body)
    {
        var result = new List<string>();
        for (var i = 0; i < body.Count; i++)
        {
            var l = body[i];
            if (l.Length == 0 || l[0] != '\\' || result.Count == 0)
            {
                result.Add(l);
                continue;
            }
            var rest = body.Skip(i + 1).Where(r => r.Length > 0 && r[0] != '\\').ToList();
            var oldAfter = rest.Any(r => r[0] is ' ' or '-');
            var newAfter = rest.Any(r => r[0] is ' ' or '+');
            var prev = result[^1];
            switch (prev[0])
            {
                case '-':
                    if (!oldAfter) result.Add(l);
                    break;
                case '+':
                    if (!newAfter) result.Add(l);
                    break;
                default:
                    if (oldAfter && newAfter) break; // both sides go on: the line simply ends in a newline
                    if (!oldAfter && !newAfter) { result.Add(l); break; }
                    // only one side goes on: that side gets the line with a newline, the other keeps it without
                    var text = prev[1..];
                    result[^1] = (newAfter ? "-" : "+") + text;
                    result.Add(l);
                    result.Add((newAfter ? "+" : "-") + text);
                    break;
            }
        }
        return result;
    }

    /// <summary>Rewrites a new-file / deleted-file header as a modification of the same path.</summary>
    private static List<string> AsModification(List<string> header)
    {
        var path = header.Select(h => h.StartsWith("+++ b/", StringComparison.Ordinal) || h.StartsWith("--- a/", StringComparison.Ordinal) ? h[6..] : null)
            .FirstOrDefault(p => p is not null);
        var result = new List<string>();
        foreach (var h in header)
        {
            if (h.StartsWith("new file mode", StringComparison.Ordinal) || h.StartsWith("deleted file mode", StringComparison.Ordinal)
                || h.StartsWith("index ", StringComparison.Ordinal)) continue;
            if (h == "--- /dev/null" && path is not null) result.Add("--- a/" + path);
            else if (h == "+++ /dev/null" && path is not null) result.Add("+++ b/" + path);
            else result.Add(h);
        }
        return result;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)$", RegexOptions.Singleline)]
    private static partial Regex HunkHeader();

    /// <summary>Fixes the @@ counts (git apply --recount would too, but a correct header keeps the patch readable),
    /// and a start of 0 (an empty side) once that side has lines.</summary>
    private static string Rehead(string original, List<string> body)
    {
        var m = HunkHeader().Match(original);
        if (!m.Success) return original;
        int oldCount = 0, newCount = 0;
        foreach (var l in body)
        {
            if (l.StartsWith('\\')) continue;
            if (l[0] != '+') oldCount++;
            if (l[0] != '-') newCount++;
        }
        var oldStart = int.Parse(m.Groups[1].Value);
        var newStart = int.Parse(m.Groups[2].Value);
        if (oldStart == 0 && oldCount > 0) oldStart = 1;
        if (newStart == 0 && newCount > 0) newStart = 1;
        return $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@{m.Groups[3].Value}";
    }
}
