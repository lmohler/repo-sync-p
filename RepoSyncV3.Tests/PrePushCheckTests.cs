using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>The per-repo command run before a push.</summary>
public sealed class PrePushCheckTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private static async Task<(TempRepo Server, TempRepo Repo)> SetupAsync()
    {
        var server = await TempRepo.InitAsync(bare: true);
        var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        await repo.CommitAsync("b.txt", "b", "second");
        return (server, repo);
    }

    [Fact]
    public async Task FailingCheck_StopsThePush()
    {
        var (server, repo) = await SetupAsync();
        using var _s = server;
        using var _r = repo;
        var actions = new RepoActions(repo.Path, new SyncProgress(new List<LogEntry>())) { PrePushCommand = "echo broken build & exit /b 3" };

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => actions.PushAsync());

        Assert.Contains("exit 3", ex.Message);
        Assert.Contains("broken build", string.Join("\n", ex.Help));
        Assert.NotEqual((await repo.GitAsync("rev-parse", "HEAD")).Trim(), (await server.GitAsync("rev-parse", "main")).Trim());
    }

    [Fact]
    public async Task PassingCheck_Pushes()
    {
        var (server, repo) = await SetupAsync();
        using var _s = server;
        using var _r = repo;
        var actions = new RepoActions(repo.Path, new SyncProgress(new List<LogEntry>())) { PrePushCommand = "exit /b 0" };

        await actions.PushAsync();

        Assert.Equal((await repo.GitAsync("rev-parse", "HEAD")).Trim(), (await server.GitAsync("rev-parse", "main")).Trim());
    }
}
