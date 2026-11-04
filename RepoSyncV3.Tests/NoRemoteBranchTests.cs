using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Start / switch branches in a repo with no remote (e.g. an SVN working copy's local git).</summary>
public sealed class NoRemoteBranchTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public async Task StartBranch_WithoutRemote_BranchesFromLocalRef()
    {
        using var repo = await TempRepo.InitAsync("work");
        await repo.CommitAsync("a.txt", "a", "base");
        var actions = new RepoActions(repo.Path, new SyncProgress(new List<LogEntry>()));

        await actions.StartBranchAsync("feature/x", "work", publish: true);

        Assert.Equal("feature/x", (await repo.GitAsync("branch", "--show-current")).Trim());

        await actions.SwitchAsync("work");
        Assert.Equal("work", (await repo.GitAsync("branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task StartBranch_MissingStartPoint_SaysSo()
    {
        using var repo = await TempRepo.InitAsync("work");
        await repo.CommitAsync("a.txt", "a", "base");
        var actions = new RepoActions(repo.Path, new SyncProgress(new List<LogEntry>()));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => actions.StartBranchAsync("feature/y", "main", publish: false));
        Assert.Contains("doesn't exist", ex.Message);
    }
}
