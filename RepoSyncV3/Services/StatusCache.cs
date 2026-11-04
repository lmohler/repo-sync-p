using System.IO;
using System.Text.Json;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>What the app last knew about one repo: local git state plus the server's pipeline and merge request.
/// Saved on exit / after a refresh so the next start can show it at once (marked stale) while the real refresh runs,
/// and compared before / after a fetch to build the "what changed" feed.</summary>
public sealed record RepoSnapshot(
    string Path, string Branch, bool Detached, string? Upstream, int Ahead, int Behind,
    int Staged, int Unstaged, int Untracked, int Conflicts, string? DefaultBranch,
    string HeadSubject, DateTime? HeadDate, DateTime? LastFetch,
    ForgePipeline? Pipeline, ForgeMergeRequest? MergeRequest, DateTime SavedAt);

/// <summary>status-cache.json next to settings.json: one <see cref="RepoSnapshot"/> per repo path.</summary>
public static class StatusCache
{
    public static readonly string DefaultFile = Path.Combine(SettingsService.SettingsDir, "status-cache.json");

    /// <summary>Snapshots keyed by repo path (case-insensitive); empty when the file is missing or unreadable.</summary>
    public static Dictionary<string, RepoSnapshot> Load(string? file = null)
    {
        var result = new Dictionary<string, RepoSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            file ??= DefaultFile;
            if (!File.Exists(file)) return result;
            foreach (var s in JsonSerializer.Deserialize<List<RepoSnapshot>>(File.ReadAllText(file)) ?? new())
                if (s.Path.Length > 0) result[s.Path] = s;
        }
        catch
        {
            // a broken cache only costs the instant start; the refresh rebuilds it
        }
        return result;
    }

    /// <returns>null on success, else why it failed.</returns>
    public static string? Save(IEnumerable<RepoSnapshot> snapshots, string? file = null)
    {
        file ??= DefaultFile;
        var temp = file + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshots.ToList()));
            File.Move(temp, file, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            return $"Could not save the status cache: {ex.Message}";
        }
    }
}

/// <summary>One line in the "what changed" feed.</summary>
/// <param name="Kind">commits, pipeline, mr - picks the icon.</param>
/// <param name="Tone">success, info, warning, error, purple (pill colors).</param>
public sealed record FeedItem(DateTime Time, string Repo, string RepoPath, string Text, string Kind, string Tone)
{
    public string TimeText => ViewModels.TimeText.Relative(Time);

    public string Icon => Kind switch
    {
        "pipeline" => "",
        "mr" => "",
        _ => "",
    };
}

/// <summary>Compares a repo's state before and after a refresh, and keeps the recent results in feed.json.</summary>
public static class ChangeFeed
{
    public const int MaxItems = 200;
    public static readonly string DefaultFile = Path.Combine(SettingsService.SettingsDir, "feed.json");

    /// <summary>The events between two snapshots of the same repo: new commits on the server for the branch,
    /// a pipeline finishing, a merge request opened, approved / ready, merged or closed.</summary>
    public static List<FeedItem> Diff(string repoName, RepoSnapshot before, RepoSnapshot after, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var items = new List<FeedItem>();
        void Add(string text, string kind, string tone) => items.Add(new FeedItem(at, repoName, after.Path, text, kind, tone));

        // only the same branch tracking the same upstream: a branch switch isn't news from the server
        if (before.Branch == after.Branch && before.Upstream == after.Upstream && after.Upstream is not null && after.Behind > before.Behind)
            Add($"{after.Behind - before.Behind} new commit(s) on {after.Branch} in {repoName}", "commits", "purple");

        if (after.Pipeline is { } p && after.Branch == before.Branch)
        {
            var old = before.Pipeline;
            var finished = p.Kind is "success" or "failed" or "canceled";
            if (finished && (old is null || old.Id != p.Id || old.Kind != p.Kind))
                Add($"Pipeline {p.Label} on {after.Branch} in {repoName}", "pipeline", p.Kind == "success" ? "success" : p.Kind == "failed" ? "error" : "warning");
        }

        if (after.MergeRequest is { } mr && after.Branch == before.Branch)
        {
            var old = before.MergeRequest is { } o && o.Iid == mr.Iid ? o : null;
            if (old is null && before.MergeRequest is null && mr.State == "opened")
                Add($"{mr.Reference} opened in {repoName}: {mr.Title}", "mr", "info");
            else if (old is not null && old.State != mr.State && mr.State is "merged" or "closed")
                Add($"{mr.Reference} {mr.State} in {repoName}", "mr", mr.State == "merged" ? "success" : "warning");
            else if (old is not null && mr.State == "opened" && old.DetailedMergeStatus == "not_approved" && mr.DetailedMergeStatus != "not_approved")
                Add($"{mr.Reference} approved in {repoName}", "mr", "success");
            else if (old is not null && mr.State == "opened" && !old.HasConflicts && mr.HasConflicts)
                Add($"{mr.Reference} now has conflicts in {repoName}", "mr", "error");
        }
        return items;
    }

    public static List<FeedItem> Load(string? file = null)
    {
        try
        {
            file ??= DefaultFile;
            return File.Exists(file) ? JsonSerializer.Deserialize<List<FeedItem>>(File.ReadAllText(file)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Saves the newest <see cref="MaxItems"/> (the list is newest first).</summary>
    public static string? Save(IEnumerable<FeedItem> items, string? file = null)
    {
        try
        {
            File.WriteAllText(file ?? DefaultFile, JsonSerializer.Serialize(items.Take(MaxItems).ToList()));
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not save the change feed: {ex.Message}";
        }
    }
}

/// <summary>Repo avatar colors: picked from the name unless the repo has its own.</summary>
public static class RepoColors
{
    // Mid-tone hues that keep white text readable in both themes.
    public static readonly string[] Palette =
    [
        "#2F7DD1", "#2E9E6B", "#B5602A", "#8A5CD1", "#C2417A",
        "#1E9AA8", "#7A8F2A", "#C7472E", "#5468D4", "#A07A1C",
    ];

    /// <summary>Stable across runs (FNV-1a; string.GetHashCode is randomized per process), case-insensitive.</summary>
    public static string ForName(string name)
    {
        uint h = 2166136261;
        foreach (var c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return Palette[h % (uint)Palette.Length];
    }

    /// <summary>"#RRGGBB" (with or without the #, any case) normalized to "#RRGGBB"; null when it isn't one.</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().TrimStart('#') ?? string.Empty;
        return v.Length == 6 && v.All(Uri.IsHexDigit) ? "#" + v.ToUpperInvariant() : null;
    }

    /// <summary>The repo's own color if valid, else the one from its name.</summary>
    public static string Resolve(string name, string? custom) => Normalize(custom) ?? ForName(name);
}
