using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>git --progress lines reach the Progress callback; the log gets the collapsed final state only.</summary>
public sealed class ProgressTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public async Task Push_ReportsProgress_LogStaysShort()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        for (var i = 0; i < 20; i++) await repo.CommitAsync($"f{i}.txt", new string('x', 2000 + i), $"c{i}");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        await repo.CommitAsync("g.txt", "g", "one more");

        var lines = new List<string>();
        var log = new List<LogEntry>();
        await new RepoActions(repo.Path, new SyncProgress(log)) { Progress = l => { lock (lines) lines.Add(l); } }.PushAsync();

        Assert.Contains(lines, l => l.Contains('%'));
        Assert.Contains(log, e => e.Message.Contains("--progress"));
        Assert.DoesNotContain(log, e => e.Message.Contains('\r'));
    }
}
