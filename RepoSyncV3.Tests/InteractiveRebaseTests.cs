using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Interactive rebase through our own todo (no editor) on a scratch repo, plus small helpers.</summary>
public sealed class InteractiveRebaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "rebase-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<LogEntry> _log = new();

    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private async Task<string> G(params string[] args)
    {
        var r = await Git.RunAsync(_dir, args);
        if (!r.Success) throw new InvalidOperationException(r.CombinedOutput);
        return r.StdOut;
    }

    private async Task<string> SetupAsync()
    {
        Directory.CreateDirectory(_dir);
        await G("init", "-q", "-b", "main");
        await G("config", "core.autocrlf", "false");
        await G("config", "user.name", "T");
        await G("config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(_dir, "base.txt"), "base\n");
        await G("add", "-A");
        await G("commit", "-q", "-m", "base");
        var onto = (await G("rev-parse", "HEAD")).Trim();
        foreach (var n in new[] { "one", "two", "three", "four" })
        {
            File.WriteAllText(Path.Combine(_dir, n + ".txt"), n + "\n");
            await G("add", "-A");
            await G("commit", "-q", "-m", $"add {n}");
        }
        return onto;
    }

    [Fact]
    public async Task Reword_Squash_Drop_Reorder()
    {
        var onto = await SetupAsync();
        var commits = await Git.GetLogAsync(_dir, $"{onto}..HEAD", 50);
        commits.Reverse();
        var steps = commits.Select(c => new RebaseStep { Hash = c.Hash, OriginalMessage = c.Subject, Message = c.Subject }).ToList();
        // one: reword; two: squash into one; three: drop; four: move first
        steps[0].Action = "reword";
        steps[0].Message = "first (reworded)";
        steps[1].Action = "fixup";
        steps[2].Action = "drop";
        var plan = new List<RebaseStep> { steps[3], steps[0], steps[1], steps[2] };

        await new RepoActions(_dir, new SyncProgress(_log)).InteractiveRebaseAsync(onto, plan);

        var subjects = (await G("log", "--format=%s", $"{onto}..HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "first (reworded)", "add four" }, subjects);
        Assert.False(File.Exists(Path.Combine(_dir, "three.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "two.txt")));
        Assert.Equal(string.Empty, (await G("status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task StoppedRewrite_KeepsMessageFolder_UntilAbort()
    {
        Directory.CreateDirectory(_dir);
        await G("init", "-q", "-b", "main");
        await G("config", "core.autocrlf", "false");
        await G("config", "user.name", "T");
        await G("config", "user.email", "t@example.com");
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "x\n");
        await G("add", "-A");
        await G("commit", "-q", "-m", "base");
        var onto = (await G("rev-parse", "HEAD")).Trim();
        foreach (var text in new[] { "a", "b" })
        {
            File.WriteAllText(file, text + "\n");
            await G("commit", "-q", "-am", $"to {text}");
        }
        var commits = await Git.GetLogAsync(_dir, $"{onto}..HEAD", 50);
        commits.Reverse();
        var steps = commits.Select(c => new RebaseStep { Hash = c.Hash, OriginalMessage = c.Subject, Message = c.Subject }).ToList();
        steps[0].Action = "reword";
        steps[0].Message = "to a (reworded)";
        var actions = new RepoActions(_dir, new SyncProgress(_log));

        // "to b" first can't apply on "x": the rebase stops, and Continue would still need the reword message file
        await Assert.ThrowsAsync<UserFacingException>(() => actions.InteractiveRebaseAsync(onto, new[] { steps[1], steps[0] }));
        var marker = Path.Combine(Git.GitDir(_dir)!, "repo-sync-rebase-dir");
        Assert.True(File.Exists(marker));
        var messages = File.ReadAllText(marker).Trim();
        Assert.True(Directory.Exists(messages));

        await actions.AbortAsync();
        Assert.False(Directory.Exists(messages));
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("feature/PROJ-123-fix-login", "PROJ-123")]
    [InlineData("proj-42_thing", "PROJ-42")]
    [InlineData("main", null)]
    [InlineData("release-2024", null)]
    [InlineData("release-2024/PROJ-7-x", "PROJ-7")]
    public void TicketFromBranch(string branch, string? expected) => Assert.Equal(expected, Git.TicketFromBranch(branch));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
