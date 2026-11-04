using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Undo of the app's own actions: snapshot refs, act, put them back.</summary>
public sealed class UndoPointTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private static RepoActions Actions(TempRepo repo) => new(repo.Path, new SyncProgress(new List<LogEntry>()));

    [Fact]
    public async Task Undo_Commit_KeepsChangesAndMovesBranchBack()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var baseHash = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        var before = (await UndoPoint.TakeAsync(repo.Path, "Commit"))!;

        await repo.CommitAsync("b.txt", "b", "second");
        Assert.True(before.DiffersFrom((await UndoPoint.TakeAsync(repo.Path, "x"))!));

        await Actions(repo).UndoToAsync(before);

        Assert.Equal(baseHash, (await repo.GitAsync("rev-parse", "HEAD")).Trim());
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")), "the undone commit's file stays on disk");
        Assert.Contains("b.txt", await repo.GitAsync("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Undo_StartBranch_SwitchesBackAndDeletesEmptyBranch()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var before = (await UndoPoint.TakeAsync(repo.Path, "Start branch"))!;
        await Actions(repo).StartBranchAsync("feature/x", "main", publish: false);

        await Actions(repo).UndoToAsync(before);

        Assert.Equal("main", (await repo.GitAsync("branch", "--show-current")).Trim());
        Assert.DoesNotContain("feature/x", await repo.GitAsync("branch", "--list"));
    }

    [Fact]
    public async Task Undo_RefusesWhenCommitsAreOnTheServer()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        var before = (await UndoPoint.TakeAsync(repo.Path, "Commit & push"))!;
        await repo.CommitAsync("b.txt", "b", "second");
        await repo.GitAsync("push");

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => Actions(repo).UndoToAsync(before));
        Assert.Contains("already on the server", ex.Message);
    }
}
