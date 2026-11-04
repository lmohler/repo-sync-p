using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>status-cache.json / feed.json round trips, the before / after feed diff, and repo avatar colors.</summary>
public class StatusCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rsv3-cache-" + Guid.NewGuid().ToString("N"));

    public StatusCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static RepoSnapshot Snap(int behind = 0, ForgePipeline? pipe = null, ForgeMergeRequest? mr = null, string branch = "main") =>
        new(@"C:\src\web-portal", branch, false, "origin/" + branch, 1, behind, 2, 3, 1, 0, "main", "Fix it", new DateTime(2026, 10, 1),
            new DateTime(2026, 10, 2), pipe, mr, new DateTime(2026, 10, 3));

    private static ForgePipeline Pipe(long id, string status) => new(id, status, "https://x/p/" + id, "main", "abcdef0123", "push", null, null);

    private static ForgeMergeRequest Mr(string state = "opened", string? status = "not_approved", bool conflicts = false) =>
        new(1, 12, "Add feed", state, false, "https://x/mr/12", conflicts, "feature/x", "main", status, null, null, null, null, null, null, null);

    [Fact]
    public void Cache_RoundTrips()
    {
        var file = Path.Combine(_dir, "status-cache.json");
        var s = Snap(behind: 4, pipe: Pipe(7, "failed"), mr: Mr());
        Assert.Null(StatusCache.Save(new[] { s }, file));

        var loaded = StatusCache.Load(file);
        var back = Assert.Single(loaded.Values);
        Assert.True(loaded.ContainsKey(@"c:\SRC\web-portal")); // paths match case-insensitively
        Assert.Equal(s with { Pipeline = null, MergeRequest = null }, back with { Pipeline = null, MergeRequest = null });
        Assert.Equal("failed", back.Pipeline!.Kind);
        Assert.Equal("!12", back.MergeRequest!.Reference);
    }

    [Fact]
    public void Cache_MissingOrBrokenFile_IsEmpty()
    {
        var file = Path.Combine(_dir, "status-cache.json");
        Assert.Empty(StatusCache.Load(file));
        File.WriteAllText(file, "{ not json");
        Assert.Empty(StatusCache.Load(file));
    }

    [Fact]
    public void Diff_NewCommitsOnServer()
    {
        var item = Assert.Single(ChangeFeed.Diff("web-portal", Snap(behind: 0), Snap(behind: 3)));
        Assert.Equal("3 new commit(s) on main in web-portal", item.Text);
        Assert.Equal("commits", item.Kind);
    }

    [Fact]
    public void Diff_BranchSwitch_IsNotNews()
    {
        Assert.Empty(ChangeFeed.Diff("web-portal", Snap(behind: 0, branch: "main"), Snap(behind: 5, branch: "work")));
    }

    [Fact]
    public void Diff_PipelineFinished()
    {
        var item = Assert.Single(ChangeFeed.Diff("work", Snap(pipe: Pipe(7, "running")), Snap(pipe: Pipe(7, "failed"))));
        Assert.Contains("failed on main", item.Text);
        Assert.Equal("error", item.Tone);
        // the same finished pipeline again is not news
        Assert.Empty(ChangeFeed.Diff("work", Snap(pipe: Pipe(7, "failed")), Snap(pipe: Pipe(7, "failed"))));
        // still running is not news either
        Assert.Empty(ChangeFeed.Diff("work", Snap(pipe: Pipe(7, "pending")), Snap(pipe: Pipe(7, "running"))));
    }

    [Fact]
    public void Diff_MergeRequestEvents()
    {
        Assert.Equal("!12 approved in web-portal", Assert.Single(ChangeFeed.Diff("web-portal", Snap(mr: Mr()), Snap(mr: Mr(status: "mergeable")))).Text);
        Assert.Equal("!12 merged in web-portal", Assert.Single(ChangeFeed.Diff("web-portal", Snap(mr: Mr()), Snap(mr: Mr("merged", "mergeable")))).Text);
        Assert.StartsWith("!12 opened", Assert.Single(ChangeFeed.Diff("web-portal", Snap(), Snap(mr: Mr()))).Text);
        Assert.Contains("conflicts", Assert.Single(ChangeFeed.Diff("web-portal", Snap(mr: Mr(status: "mergeable")), Snap(mr: Mr(status: "mergeable", conflicts: true)))).Text);
        Assert.Empty(ChangeFeed.Diff("web-portal", Snap(mr: Mr()), Snap(mr: Mr())));
    }

    [Fact]
    public void Feed_RoundTrips_AndKeepsTheNewest()
    {
        var file = Path.Combine(_dir, "feed.json");
        var items = Enumerable.Range(0, ChangeFeed.MaxItems + 5)
            .Select(i => new FeedItem(new DateTime(2026, 10, 30).AddMinutes(-i), "r", "p", $"item {i}", "commits", "purple")).ToList();
        Assert.Null(ChangeFeed.Save(items, file));
        var back = ChangeFeed.Load(file);
        Assert.Equal(ChangeFeed.MaxItems, back.Count);
        Assert.Equal(items[0], back[0]);
    }

    [Fact]
    public void AvatarColor_IsStable_AndOverridable()
    {
        Assert.Equal(RepoColors.ForName("web-portal"), RepoColors.ForName("Web-Portal"));
        Assert.Contains(RepoColors.ForName("repo-sync-v3"), RepoColors.Palette);
        Assert.Equal("#ABCDEF", RepoColors.Resolve("web-portal", "abcdef"));
        Assert.Equal("#123456", RepoColors.Normalize(" #123456 "));
        Assert.Null(RepoColors.Normalize("#12345G"));
        Assert.Equal(RepoColors.ForName("web-portal"), RepoColors.Resolve("web-portal", "nope"));
    }
}
