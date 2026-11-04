using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>The session's undo history: record actions, describe what moved, undo an older entry safely.</summary>
public sealed class UndoHistoryTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private static RepoActions Actions(TempRepo repo) => new(repo.Path, new SyncProgress(new List<LogEntry>()));

    /// <summary>What RunRepoAsync does: snapshot, act, snapshot, record.</summary>
    private static async Task<UndoHistoryEntry> RecordAsync(UndoHistory history, TempRepo repo, string title, Func<Task> action)
    {
        var before = await UndoPoint.TakeAsync(repo.Path, title);
        await action();
        var after = await UndoPoint.TakeAsync(repo.Path, title);
        return history.Record(title, "test", repo.Path, before, after);
    }

    [Fact]
    public async Task Record_DescribesMovesAndKeepsNewestFirst()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var history = new UndoHistory();

        var fetch = await RecordAsync(history, repo, "Fetch", () => Task.CompletedTask);
        var branch = await RecordAsync(history, repo, "Start branch", () => repo.GitAsync("switch", "-c", "feature/x"));

        Assert.Same(branch, history.Entries[0]);
        Assert.False(fetch.CanUndo);
        Assert.Equal("No branch moved", fetch.Changes);
        Assert.True(branch.CanUndo);
        Assert.Contains("switched main -> feature/x", branch.Changes);
        Assert.Contains("created feature/x", branch.Changes);
    }

    [Fact]
    public async Task Check_CleanAndUnmoved_NoWarnings()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var history = new UndoHistory();
        var e = await RecordAsync(history, repo, "Commit", () => repo.CommitAsync("b.txt", "b", "second"));

        Assert.Empty(await UndoHistory.CheckAsync(e));
    }

    [Fact]
    public async Task Check_WarnsWhenRefsMovedSinceAndTreeIsDirty()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var history = new UndoHistory();
        var first = await RecordAsync(history, repo, "Commit", () => repo.CommitAsync("b.txt", "b", "second"));
        await repo.CommitAsync("c.txt", "c", "third"); // outside the app
        repo.Write("a.txt", "edited");

        var warnings = await UndoHistory.CheckAsync(first);

        Assert.Equal(2, warnings.Count);
        Assert.Contains("moved since", warnings[0]);
        Assert.Contains("uncommitted", warnings[1]);
    }

    [Fact]
    public async Task Check_RefusesUnmovedAndAlreadyUndone()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var history = new UndoHistory();
        var fetch = await RecordAsync(history, repo, "Fetch", () => Task.CompletedTask);
        await Assert.ThrowsAsync<UserFacingException>(() => UndoHistory.CheckAsync(fetch));

        var commit = await RecordAsync(history, repo, "Commit", () => repo.CommitAsync("b.txt", "b", "second"));
        commit.Undone = true;
        await Assert.ThrowsAsync<UserFacingException>(() => UndoHistory.CheckAsync(commit));
    }

    [Fact]
    public async Task Undo_OlderEntry_RestoresBranchesAndKeepsUncommittedWork()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var baseHash = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        var history = new UndoHistory();

        var start = await RecordAsync(history, repo, "Start branch", () => Actions(repo).StartBranchAsync("feature/y", "main", publish: false));
        await RecordAsync(history, repo, "Switch", () => repo.GitAsync("switch", "main"));
        await RecordAsync(history, repo, "Commit", () => repo.CommitAsync("b.txt", "b", "on main"));
        repo.Write("notes.txt", "keep me");

        // undo the oldest one: everything goes back to before "Start branch"
        Assert.NotEmpty(await UndoHistory.CheckAsync(start));
        await Actions(repo).UndoToAsync(start.Before!);

        Assert.Equal("main", (await repo.GitAsync("branch", "--show-current")).Trim());
        Assert.Equal(baseHash, (await repo.GitAsync("rev-parse", "HEAD")).Trim());
        Assert.DoesNotContain("feature/y", await repo.GitAsync("branch", "--list"));
        Assert.True(File.Exists(Path.Combine(repo.Path, "notes.txt")), "untracked work is kept");
    }
}
