namespace RepoSyncV3.Services;

/// <summary>
/// Where HEAD and every local branch pointed before an action ran, so the action can be taken back with one click.
/// Only refs are recorded: stashes, tags and the server are left alone, and undo refuses when it would drop
/// commits that are already on the server.
/// </summary>
public sealed record UndoPoint(string Title, string? Branch, string Head, IReadOnlyDictionary<string, string> Heads)
{
    public static async Task<UndoPoint?> TakeAsync(string path, string title, CancellationToken ct = default)
    {
        var head = (await Git.RunAsync(path, new[] { "rev-parse", "-q", "--verify", "HEAD" }, ct)).StdOut.Trim();
        if (head.Length == 0) return null; // no commits yet
        var branch = (await Git.RunAsync(path, new[] { "symbolic-ref", "-q", "--short", "HEAD" }, ct)).StdOut.Trim();
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = await Git.RunAsync(path, new[] { "for-each-ref", "refs/heads", "--format=%(refname:short) %(objectname)" }, ct);
        foreach (var line in refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.LastIndexOf(' ');
            if (i > 0) heads[line[..i]] = line[(i + 1)..];
        }
        return new UndoPoint(title, branch.Length == 0 ? null : branch, head, heads);
    }

    /// <summary>True when <paramref name="other"/> is a different state (the action moved something worth undoing).</summary>
    public bool DiffersFrom(UndoPoint other) =>
        Branch != other.Branch || Head != other.Head || Heads.Count != other.Heads.Count
        || Heads.Any(kv => !other.Heads.TryGetValue(kv.Key, out var h) || h != kv.Value);
}
