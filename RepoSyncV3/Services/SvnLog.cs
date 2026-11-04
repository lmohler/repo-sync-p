using System.Globalization;
using System.Xml.Linq;

namespace RepoSyncV3.Services;

/// <summary>Where one step of an SVN round trip is (the SVN tab's progress panel).</summary>
public enum SvnStepState { Pending, Running, Done, Failed, Skipped }

/// <summary>One step update from <see cref="SvnWorkflow"/>: the first report for an id adds the step (Pending),
/// later ones move it on. <paramref name="Message"/> is the outcome ("Updated SVN r5 -> r6") or the error.</summary>
public sealed record SvnStepProgress(string Id, string Title, SvnStepState State, string? Message = null);

/// <summary>A path changed in an SVN revision ('svn log -v'): A/M/D/R plus the repository path.</summary>
public sealed record SvnChangedPath(char Action, string Path, string? CopyFrom = null) : Models.ITreeFile
{
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/'));
    public string Folder => Path.TrimEnd('/') is var p && p.LastIndexOf('/') is > 0 and var i ? p[..i] : string.Empty;
    /// <summary>Indent level in the tree view (set by FileTreeView).</summary>
    public int TreeDepth { get; set; }
    public string Text => CopyFrom is null ? $"{Action}  {Path}" : $"{Action}  {Path}  (from {CopyFrom})";
}

/// <summary>One SVN revision on the SVN tab's log, with the git mirror commit that holds it (if any).</summary>
public sealed class SvnRevision
{
    public long Revision { get; init; }
    public string Author { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<SvnChangedPath> Paths { get; init; } = new();

    /// <summary>The svn-rNNNN tag whose commit contains this revision: its own tag, or the next later one when
    /// 'svn update' jumped over it (e.g. r5 -> r8 records only svn-r8, which holds r6 and r7 too).</summary>
    public string? MirrorTag { get; set; }
    public string? MirrorHash { get; set; }
    public bool MirrorExact { get; set; }

    public string RevisionText => $"r{Revision}";
    public string Subject => Message.Replace("\r\n", "\n").Split('\n')[0].Trim();
    public bool IsMirrored => MirrorHash is not null;
    public string MirrorText => MirrorTag is null ? "not mirrored yet" : MirrorExact ? MirrorTag : $"in {MirrorTag}";
}

/// <summary>Reads 'svn log' and lines its revisions up with the svn-rNNNN mirror tags on git main.</summary>
public static class SvnLog
{
    public const string TagPrefix = "svn-r";

    /// <summary>Parses 'svn log --xml [-v]'. Missing author/date/message (e.g. no read access) stay empty.</summary>
    public static List<SvnRevision> ParseLogXml(string xml)
    {
        var result = new List<SvnRevision>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        foreach (var entry in XDocument.Parse(xml).Descendants("logentry"))
        {
            if (!long.TryParse(entry.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) continue;
            var date = DateTimeOffset.TryParse(entry.Element("date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d.LocalDateTime
                : DateTime.MinValue;
            result.Add(new SvnRevision
            {
                Revision = revision,
                Author = entry.Element("author")?.Value ?? string.Empty,
                Date = date,
                Message = (entry.Element("msg")?.Value ?? string.Empty).Trim(),
                Paths = entry.Element("paths")?.Elements("path").Select(p => new SvnChangedPath(
                    (p.Attribute("action")?.Value ?? "?")[0],
                    p.Value,
                    p.Attribute("copyfrom-path")?.Value is { } from ? $"{from}@{p.Attribute("copyfrom-rev")?.Value}" : null)).ToList() ?? new(),
            });
        }
        return result;
    }

    /// <summary>Parses 'git for-each-ref --format="%(refname:short) %(objectname) %(*objectname)" refs/tags/svn-r*'
    /// into revision -> commit. Annotated tags (what the mirror writes) use the peeled commit, light ones their own.</summary>
    public static SortedDictionary<long, string> ParseMirrorTags(string forEachRef)
    {
        var tags = new SortedDictionary<long, string>();
        foreach (var line in forEachRef.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            if (!long.TryParse(parts[0][TagPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) continue;
            tags[revision] = parts.Length >= 3 ? parts[2] : parts[1];
        }
        return tags;
    }

    /// <summary>Points each revision at the first mirror tag at or after it (its own, or the later one that
    /// took it in). Revisions newer than every tag are not mirrored yet.</summary>
    public static void MapMirrors(IEnumerable<SvnRevision> revisions, SortedDictionary<long, string> tags)
    {
        var ordered = tags.Keys.ToList();
        foreach (var rev in revisions)
        {
            var i = ordered.BinarySearch(rev.Revision);
            if (i < 0) i = ~i;
            if (i >= ordered.Count)
            {
                rev.MirrorTag = rev.MirrorHash = null;
                rev.MirrorExact = false;
                continue;
            }
            var tagRevision = ordered[i];
            rev.MirrorTag = TagPrefix + tagRevision.ToString(CultureInfo.InvariantCulture);
            rev.MirrorHash = tags[tagRevision];
            rev.MirrorExact = tagRevision == rev.Revision;
        }
    }

    /// <summary>The latest <paramref name="limit"/> revisions on the server (HEAD down), so ones not synced yet show too.
    /// Needs the server; throws a <see cref="UserFacingException"/> when svn fails.</summary>
    public static async Task<List<SvnRevision>> GetLogAsync(string wc, int limit, CancellationToken ct = default)
    {
        var result = await Svn.RunAsync(wc, new[] { "log", "--xml", "-v", "--non-interactive", "-r", "HEAD:1", "-l", limit.ToString(CultureInfo.InvariantCulture), "." }, ct: ct);
        if (!result.Success)
            throw new UserFacingException("'svn log' failed.", new[] { result.CombinedOutput, "If SVN needs your password, run 'svn log' once in a terminal so the login is saved." });
        return ParseLogXml(result.StdOut);
    }

    public static async Task<SortedDictionary<long, string>> GetMirrorTagsAsync(string wc, CancellationToken ct = default)
    {
        var result = await Git.RunAsync(wc, new[] { "for-each-ref", "--format=%(refname:short) %(objectname) %(*objectname)", "refs/tags/" + TagPrefix + "*" }, ct);
        return result.Success ? ParseMirrorTags(result.StdOut) : new SortedDictionary<long, string>();
    }
}
