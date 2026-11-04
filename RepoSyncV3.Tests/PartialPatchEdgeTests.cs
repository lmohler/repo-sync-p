using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Line staging on the awkward files: CRLF, new (untracked / staged) files, no newline at EOF, and a
/// discard that leaves other added lines of the same hunk alone.</summary>
public sealed class PartialPatchEdgeTests
{
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private static RepoActions Actions(TempRepo repo) => new(repo.Path, new SyncProgress(new List<LogEntry>()));

    private static async Task<TempRepo> RepoAsync()
    {
        var repo = await TempRepo.InitAsync();
        await repo.GitAsync("config", "core.autocrlf", "false");
        return repo;
    }

    /// <summary>Picks diff lines by their exact text (without the CR), numbered the way the Changes tab numbers them.</summary>
    private static HashSet<(int, int)> Pick(string raw, params string[] texts) =>
        Git.ParseDiff(raw).Where(d => d.IsChange && texts.Contains(d.Text)).Select(d => (d.Hunk, d.Index)).ToHashSet();

    private static async Task<string> RawAsync(TempRepo repo, string file, bool staged) =>
        await Git.GetRawFileDiffAsync(repo.Path, new FileChange { Path = file, Code = 'M', Staged = staged });

    private static string Bytes(string text) => text.Replace("\r", "<CR>").Replace("\n", "<LF>");

    [Fact]
    public async Task Crlf_StageOneLine_KeepsCrs()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("c.txt", "one\r\ntwo\r\nthree\r\n", "base");
        repo.Write("c.txt", "one\r\nTWO\r\nthree\r\nfour\r\n");
        var raw = await RawAsync(repo, "c.txt", false);

        var patch = PartialPatch.Build(raw, Pick(raw, "+four"), reverse: false)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false);

        Assert.Equal(Bytes("one\r\ntwo\r\nthree\r\nfour\r\n"), Bytes(await repo.GitAsync("show", ":c.txt")));
        Assert.Contains("+TWO", await repo.GitAsync("diff"));
    }

    [Fact]
    public async Task Crlf_DiscardOneLine_RestoresWithCr()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("c.txt", "one\r\ntwo\r\nthree\r\n", "base");
        repo.Write("c.txt", "one\r\nTWO\r\nthree\r\nfour\r\n");
        var raw = await RawAsync(repo, "c.txt", false);

        var patch = PartialPatch.ForDiscard(raw, Pick(raw, "-two", "+TWO"))!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false, toWorktree: true);

        Assert.Equal(Bytes("one\r\ntwo\r\nthree\r\nfour\r\n"), Bytes(File.ReadAllText(Path.Combine(repo.Path, "c.txt"))));
    }

    [Fact]
    public async Task Discard_LeavesUnpickedAddedLinesOfTheSameHunk()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("f.txt", "a\nb\nc\n", "base");
        repo.Write("f.txt", "a\nX\nb\nY\nc\n");
        var raw = await RawAsync(repo, "f.txt", false);
        Assert.Single(Git.ParseDiff(raw), d => d.Kind == DiffKind.Hunk);

        var patch = PartialPatch.ForDiscard(raw, Pick(raw, "+X"))!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false, toWorktree: true);

        Assert.Equal("a\nb\nY\nc\n", File.ReadAllText(Path.Combine(repo.Path, "f.txt")));
    }

    [Fact]
    public async Task UntrackedFile_StageSomeLines()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("base.txt", "x\n", "base");
        repo.Write("new.txt", "l1\r\nl2\r\nl3\r\n");
        var raw = await PartialPatch.RawNewFileDiffAsync(repo.Path, "new.txt");
        // the Changes tab shows untracked files by line number: line N = index N-1 of hunk 0
        var patch = PartialPatch.Build(raw, new HashSet<(int, int)> { (0, 0), (0, 2) }, reverse: false)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false);

        Assert.Equal(Bytes("l1\r\nl3\r\n"), Bytes(await repo.GitAsync("show", ":new.txt")));
        Assert.Equal("l1\r\nl2\r\nl3\r\n", File.ReadAllText(Path.Combine(repo.Path, "new.txt")));
    }

    [Fact]
    public async Task UntrackedFile_DiscardSomeLines()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("base.txt", "x\n", "base");
        repo.Write("new.txt", "l1\nl2\nl3\n");
        var raw = await PartialPatch.RawNewFileDiffAsync(repo.Path, "new.txt");
        var patch = PartialPatch.ForDiscard(raw, new HashSet<(int, int)> { (0, 1) })!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false, toWorktree: true);

        Assert.Equal("l1\nl3\n", File.ReadAllText(Path.Combine(repo.Path, "new.txt")));
    }

    [Fact]
    public async Task StagedNewFile_UnstageSomeLines()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("base.txt", "x\n", "base");
        repo.Write("new.txt", "l1\nl2\nl3\n");
        await repo.GitAsync("add", "new.txt");
        var raw = await RawAsync(repo, "new.txt", true);
        Assert.Contains("new file mode", raw);

        var patch = PartialPatch.Build(raw, Pick(raw, "+l2"), reverse: true)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: true);

        Assert.Equal("l1\nl3\n", await repo.GitAsync("show", ":new.txt"));
    }

    [Fact]
    public async Task StagedNewFile_UnstageAllLines_LeavesItUntracked()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("base.txt", "x\n", "base");
        repo.Write("new.txt", "l1\nl2\n");
        await repo.GitAsync("add", "new.txt");
        var raw = await RawAsync(repo, "new.txt", true);

        var patch = PartialPatch.Build(raw, Pick(raw, "+l1", "+l2"), reverse: true)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: true);

        Assert.Contains("?? new.txt", await repo.GitAsync("status", "--porcelain"));
    }

    [Fact]
    public async Task NoNewlineAtEof_StageOnlyTheAddedLastLine()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("e.txt", "a\nb", "base");
        repo.Write("e.txt", "a\nB\nc");
        var raw = await RawAsync(repo, "e.txt", false);
        Assert.Contains("No newline at end of file", raw);

        // stage only "+c": b stays, but now needs a newline so c can follow it
        var patch = PartialPatch.Build(raw, Pick(raw, "+c"), reverse: false)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false);

        Assert.Equal("a\nb\nc", await repo.GitAsync("show", ":e.txt"));
    }

    [Fact]
    public async Task NoNewlineAtEof_StageOnlyTheRemovedLine()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("e.txt", "a\nb", "base");
        repo.Write("e.txt", "a\nB\nc");
        var raw = await RawAsync(repo, "e.txt", false);

        var patch = PartialPatch.Build(raw, Pick(raw, "-b"), reverse: false)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: false);

        Assert.Equal("a\n", await repo.GitAsync("show", ":e.txt"));
    }

    [Fact]
    public async Task NoNewlineAtEof_UnstageTheRemovalOnly()
    {
        using var repo = await RepoAsync();
        await repo.CommitAsync("e.txt", "a\nb", "base");
        repo.Write("e.txt", "a\nB\nc");
        await repo.GitAsync("add", "e.txt");
        var raw = await RawAsync(repo, "e.txt", true);

        // put b back in the index, keep B and c staged
        var patch = PartialPatch.Build(raw, Pick(raw, "-b"), reverse: true)!;
        await Actions(repo).ApplyPatchAsync(patch, reverse: true);

        Assert.Equal("a\nb\nB\nc", await repo.GitAsync("show", ":e.txt"));
    }
}
