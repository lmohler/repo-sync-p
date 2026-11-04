using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Stage / unstage / discard single lines through PartialPatch + git apply, on a scratch repo.</summary>
public sealed class PartialPatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "patch-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string FilePath => Path.Combine(_dir, "f.txt");

    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_dir);
        await G("init", "-q", "-b", "main");
        await G("config", "core.autocrlf", "false");
        await G("config", "user.name", "T");
        await G("config", "user.email", "t@example.com");
        File.WriteAllText(FilePath, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n");
        await G("add", "-A");
        await G("commit", "-q", "-m", "base");
        // two hunks: change line 2, change line 25, and add a line after 25
        var lines = File.ReadAllLines(FilePath).ToList();
        lines[1] = "line 2 CHANGED";
        lines[24] = "line 25 CHANGED";
        lines.Insert(25, "added after 25");
        File.WriteAllText(FilePath, string.Join("\n", lines) + "\n");
    }

    private static FileChange Change(bool staged) => new() { Path = "f.txt", Code = 'M', Staged = staged };

    private async Task<List<DiffLine>> Diff(bool staged) => Git.ParseDiff(await Git.GetRawFileDiffAsync(_dir, Change(staged)));

    [Fact]
    public async Task StageOneLine_LeavesTheRest()
    {
        await SetupAsync();
        var diff = await Diff(staged: false);
        Assert.Equal(2, diff.Count(d => d.Kind == DiffKind.Hunk));
        var pick = diff.Where(d => d.Text == "+added after 25").Select(d => (d.Hunk, d.Index)).ToHashSet();

        var patch = PartialPatch.Build(await Git.GetRawFileDiffAsync(_dir, Change(false)), pick, reverse: false)!;
        await new RepoActions(_dir, new SyncProgress(_log)).ApplyPatchAsync(patch, reverse: false);

        var staged = await G("diff", "--cached");
        Assert.Contains("+added after 25", staged);
        Assert.DoesNotContain("CHANGED", staged);
        var unstaged = await G("diff");
        Assert.Contains("+line 2 CHANGED", unstaged);
        Assert.Contains("+line 25 CHANGED", unstaged);
    }

    [Fact]
    public async Task UnstageOneHunk_KeepsTheOther()
    {
        await SetupAsync();
        await G("add", "f.txt");
        var diff = await Diff(staged: true);
        var first = diff.Where(d => d.Hunk == 0 && d.IsChange).Select(d => (d.Hunk, d.Index)).ToHashSet();

        var patch = PartialPatch.Build(await Git.GetRawFileDiffAsync(_dir, Change(true)), first, reverse: true)!;
        await new RepoActions(_dir, new SyncProgress(_log)).ApplyPatchAsync(patch, reverse: true);

        var staged = await G("diff", "--cached");
        Assert.DoesNotContain("line 2 CHANGED", staged);
        Assert.Contains("+line 25 CHANGED", staged);
        Assert.Contains("+line 2 CHANGED", await G("diff"));
    }

    [Fact]
    public async Task DiscardOneLine_RestoresIt()
    {
        await SetupAsync();
        var diff = await Diff(staged: false);
        var pick = diff.Where(d => d.Text is "-line 2" or "+line 2 CHANGED").Select(d => (d.Hunk, d.Index)).ToHashSet();

        var patch = PartialPatch.Build(await Git.GetRawFileDiffAsync(_dir, Change(false)), pick, reverse: false)!;
        await new RepoActions(_dir, new SyncProgress(_log)).ApplyPatchAsync(patch, reverse: false, toWorktree: true);

        var text = File.ReadAllText(FilePath);
        Assert.Contains("line 2\n", text);
        Assert.Contains("line 25 CHANGED", text);
    }

    [Fact]
    public void NothingSelected_ReturnsNull()
    {
        Assert.Null(PartialPatch.Build("diff --git a/x b/x\n@@ -1 +1 @@\n-a\n+b\n", new HashSet<(int, int)>(), reverse: false));
    }

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
