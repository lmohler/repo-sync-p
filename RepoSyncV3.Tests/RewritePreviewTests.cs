using System.IO;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>The "what will this do" lists shown before reset, rebase, interactive rebase and force push.</summary>
public sealed class RewritePreviewTests
{
    private static async Task<string> Head(TempRepo repo, string rev = "HEAD") => (await repo.GitAsync("rev-parse", rev)).Trim();

    [Fact]
    public async Task HardReset_ListsDroppedCommitsAndLostFiles()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var target = await Head(repo);
        await repo.CommitAsync("b.txt", "b", "second");
        await repo.CommitAsync("c.txt", "c", "third");
        repo.Write("a.txt", "edited");
        repo.Write("untracked.txt", "new");

        var p = await RewritePreview.ForResetAsync(repo.Path, target, "hard");

        var dropped = p.Section("Commits taken off")!;
        Assert.Equal(2, dropped.Items.Count);
        Assert.Contains(dropped.Items, i => i.EndsWith("third"));
        var lost = p.Section("Uncommitted")!;
        Assert.Contains(lost.Items, i => i.EndsWith("a.txt"));
        Assert.DoesNotContain(lost.Items, i => i.Contains("untracked.txt"));
        Assert.True(p.Danger);
        Assert.False(p.NothingToDo);
    }

    [Fact]
    public async Task SoftReset_NoLostFilesSection()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var target = await Head(repo);
        await repo.CommitAsync("b.txt", "b", "second");
        repo.Write("a.txt", "edited");

        var p = await RewritePreview.ForResetAsync(repo.Path, target, "soft");

        Assert.Single(p.Section("Commits taken off")!.Items);
        Assert.Null(p.Section("Uncommitted"));
        Assert.False(p.Danger);
    }

    [Fact]
    public async Task Reset_FlagsCommitsAlreadyOnTheServer()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var target = await Head(repo);
        await repo.CommitAsync("b.txt", "b", "pushed one");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        await repo.CommitAsync("c.txt", "c", "local one");

        var p = await RewritePreview.ForResetAsync(repo.Path, target, "mixed");

        var pushed = p.Section("Already on the server")!;
        Assert.Single(pushed.Items);
        Assert.EndsWith("pushed one", pushed.Items[0]);
    }

    [Fact]
    public async Task Rebase_ListsRewrittenAndIncoming()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        await repo.GitAsync("switch", "-c", "feature");
        await repo.CommitAsync("f1.txt", "1", "feature one");
        await repo.CommitAsync("f2.txt", "2", "feature two");
        await repo.GitAsync("switch", "main");
        await repo.CommitAsync("m.txt", "m", "main moved");
        await repo.GitAsync("switch", "feature");

        var p = await RewritePreview.ForRebaseAsync(repo.Path, "main");

        Assert.Equal(2, p.Section("Commits rewritten")!.Items.Count);
        Assert.Contains(p.Section("Commits from 'main'")!.Items, i => i.EndsWith("main moved"));
        Assert.False(p.NothingToDo);
        Assert.True((await RewritePreview.ForRebaseAsync(repo.Path, "feature")).NothingToDo);
    }

    [Fact]
    public async Task InteractiveRebase_DropAndRewriteFromFirstChange()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        var onto = await Head(repo);
        await repo.CommitAsync("1.txt", "1", "one");
        await repo.CommitAsync("2.txt", "2", "two");
        await repo.CommitAsync("3.txt", "3", "three");
        var hashes = (await repo.GitAsync("rev-list", "--reverse", $"{onto}..HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var subjects = new[] { "one", "two", "three" };
        var plan = hashes.Select((h, i) => new RebaseStep { Hash = h, OriginalMessage = subjects[i], Message = subjects[i] }).ToList();

        Assert.True((await RewritePreview.ForInteractiveRebaseAsync(repo.Path, onto, plan, hashes)).NothingToDo);

        plan[1].Action = "drop";
        var p = await RewritePreview.ForInteractiveRebaseAsync(repo.Path, onto, plan, hashes);
        Assert.Single(p.Section("Commits dropped")!.Items);
        var rewritten = p.Section("Commits rewritten")!.Items;
        Assert.Single(rewritten); // "one" keeps its hash, "two" is gone, only "three" is re-made
        Assert.EndsWith("three", rewritten[0]);
        Assert.True(p.Danger);
    }

    [Fact]
    public async Task ForcePush_ListsServerCommitsOverwritten()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        await repo.CommitAsync("b.txt", "b", "will be amended");
        await repo.GitAsync("push");
        await repo.GitAsync("commit", "--amend", "-m", "amended");

        var p = await RewritePreview.ForForcePushAsync(repo.Path, "origin", "main");

        Assert.Contains(p.Section("Server commits overwritten")!.Items, i => i.EndsWith("will be amended"));
        Assert.Contains(p.Section("Your commits")!.Items, i => i.EndsWith("amended"));
        Assert.Contains("force-with-lease", p.Summary);
        Assert.True(p.Danger);
    }

    [Fact]
    public async Task ForcePush_UnpublishedBranch_OverwritesNothing()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "base");
        await repo.GitAsync("remote", "add", "origin", server.Path);

        var p = await RewritePreview.ForForcePushAsync(repo.Path, "origin", "main");

        Assert.Null(p.Section("Server commits overwritten"));
        Assert.False(p.Danger);
        Assert.True(File.Exists(Path.Combine(repo.Path, "a.txt")));
    }
}
