using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

public enum HitKind
{
    Code,
    Commit,
    Branch,
    MergeRequest,
}

/// <summary>
/// One result of the Search all repos window. <see cref="Key"/> is what opening it needs: the file for code,
/// the full hash for a commit, the branch name (remote ones as "origin/x"), or the merge request iid.
/// </summary>
public sealed record GlobalHit(HitKind Kind, string Repo, string RepoPath, string Title, string Detail, string Key, int Line = 0,
    DateTime? Date = null, bool Merged = false)
{
    public string KindText => Kind switch
    {
        HitKind.Code => "Code",
        HitKind.Commit => "Commit",
        HitKind.Branch => "Branch",
        _ => "MR / PR",
    };

    /// <summary>Segoe MDL2 glyph for the kind column.</summary>
    public string Glyph => Kind switch
    {
        HitKind.Code => "",
        HitKind.Commit => "",
        HitKind.Branch => "",
        _ => "",
    };

    public string DateText => Date is { } d ? d.ToString("yyyy-MM-dd") : string.Empty;

    public static GlobalHit FromCode(SearchHit h) =>
        new(HitKind.Code, h.Repo, h.RepoPath, $"{h.File}:{h.Line}", h.Text, h.File, h.Line);
}

/// <summary>A repo as the search sees it (no view model, so the service stays testable).</summary>
public sealed record SearchRepo(string Name, string Path, string? RemoteName, ForgeRemote? Remote);

/// <summary>
/// Search all repos for commits (subject, hash, author), branches (local and remote) and merge / pull requests
/// (open and merged). Code search stays in <see cref="RepoSearch"/>. Matching is plain: every word typed must
/// appear somewhere in the fields, case-insensitive; a hash matches by prefix.
/// </summary>
public static class GlobalSearch
{
    public const int CommitsScanned = 5000;
    public const int MaxPerKindPerRepo = 100;

    /// <summary>Every space-separated word of the query appears in one of the fields (any order, any case).</summary>
    public static bool Matches(string query, params string?[] fields)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => fields.Any(f => f is not null && f.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool CommitMatches(CommitInfo c, string query)
    {
        var q = query.Trim();
        if (q.Length >= 4 && c.Hash.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return true;
        return Matches(q, c.Subject, c.Author, c.Email, c.ShortHash);
    }

    public static bool MergeRequestMatches(ForgeMergeRequest mr, string query) =>
        Matches(query, mr.Title, mr.Reference, mr.SourceBranch, mr.TargetBranch, mr.AuthorName, mr.Author?.Username);

    public static async Task<List<GlobalHit>> SearchAsync(IEnumerable<SearchRepo> repos, string query, bool commits, bool branches, bool mergeRequests,
        CancellationToken ct = default)
    {
        if (query.Trim().Length == 0) return new List<GlobalHit>();
        using var gate = new SemaphoreSlim(6);
        var tasks = repos.Where(r => System.IO.Directory.Exists(r.Path)).Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var hits = new List<GlobalHit>();
                if (commits) hits.AddRange(await CommitsAsync(r, query, ct));
                if (branches) hits.AddRange(await BranchesAsync(r, query, ct));
                if (mergeRequests) hits.AddRange(await MergeRequestsAsync(r, query, ct));
                return hits;
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(tasks)).SelectMany(h => h).ToList();
    }

    private static async Task<IEnumerable<GlobalHit>> CommitsAsync(SearchRepo r, string query, CancellationToken ct)
    {
        var log = await Git.GetLogAsync(r.Path, "--all", CommitsScanned, ct);
        return log.Where(c => CommitMatches(c, query)).Take(MaxPerKindPerRepo)
                  .Select(c => new GlobalHit(HitKind.Commit, r.Name, r.Path, c.Subject, $"{c.ShortHash}  {c.Author}", c.Hash, Date: c.Date));
    }

    private static async Task<IEnumerable<GlobalHit>> BranchesAsync(SearchRepo r, string query, CancellationToken ct)
    {
        var list = await Git.GetLocalBranchesAsync(r.Path, ct);
        var localNames = list.Select(b => b.Name).ToHashSet();
        if (r.RemoteName is { } remote)
        {
            // a server branch only when there is no local one of the same name (that one opens either way)
            list.AddRange((await Git.GetRemoteBranchesAsync(r.Path, remote, ct))
                .Where(b => !localNames.Contains(b.Name[(remote.Length + 1)..])));
        }
        return list.Where(b => Matches(query, b.Name, b.Author)).Take(MaxPerKindPerRepo)
                   .Select(b => new GlobalHit(HitKind.Branch, r.Name, r.Path, b.Name,
                       (b.HasLocal ? (b.IsCurrent ? "current, " : string.Empty) + b.Tracking : "on the server") + (b.Subject.Length > 0 ? $"  -  {b.Subject}" : string.Empty),
                       b.Name, Date: b.Date));
    }

    private static async Task<IEnumerable<GlobalHit>> MergeRequestsAsync(SearchRepo r, string query, CancellationToken ct)
    {
        if (r.Remote is null || Forges.For(r.Remote) is not { } client) return Array.Empty<GlobalHit>();
        var hits = new List<GlobalHit>();
        foreach (var scope in new[] { MergeRequestScope.AllOpen, MergeRequestScope.Merged })
        {
            try
            {
                var mrs = await client.ListMergeRequestsAsync(r.Remote, scope, ct);
                hits.AddRange(mrs.Where(m => MergeRequestMatches(m, query)).Take(MaxPerKindPerRepo)
                    .Select(m => new GlobalHit(HitKind.MergeRequest, r.Name, r.Path, $"{m.Reference} {m.Title}",
                        $"{m.Branches}  {m.AuthorName}  ({m.State})", m.Iid.ToString(), Date: m.UpdatedAt, Merged: scope == MergeRequestScope.Merged)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // no token, server down, project gone: the other repos' results still count
            }
        }
        return hits.DistinctBy(h => h.Key);
    }
}
