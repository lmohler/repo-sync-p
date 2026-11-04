using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Linked worktrees: discovery from either side, the add/remove/prune actions, and the shared-refs watch filter.</summary>
public class WorktreeTests
{
    private static RepoActions Actions(string path) => new(path, new Progress<LogEntry>());

    private static string Sibling(TempRepo repo, string name) =>
        Path.Combine(Path.GetDirectoryName(repo.Path)!, Path.GetFileName(repo.Path) + name);

    [Fact]
    public async Task AddWorktree_NewBranch_IsListedFromBothSides()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        var folder = Sibling(repo, "-work");
        try
        {
            await Actions(repo.Path).AddWorktreeAsync(folder, "work", "main");

            var fromMain = await Git.GetWorktreesAsync(repo.Path);
            Assert.Equal(2, fromMain.Count);
            Assert.True(fromMain[0].IsMain && fromMain[0].IsCurrent);
            Assert.Equal("main", fromMain[0].Branch);
            Assert.Equal(folder, fromMain[1].Path, ignoreCase: true);
            Assert.Equal("work", fromMain[1].Branch);
            Assert.False(fromMain[1].IsCurrent);

            var fromWork = await Git.GetWorktreesAsync(folder);
            Assert.True(fromWork[1].IsCurrent);
            Assert.False(fromWork[0].IsCurrent);

            Assert.Equal(repo.Path, Git.MainWorktreePath(folder), ignoreCase: true);
            Assert.Null(Git.MainWorktreePath(repo.Path));
            Assert.Equal(Path.Combine(repo.Path, ".git"), Git.CommonDir(folder), ignoreCase: true);
            Assert.Equal("main", (await Git.GetStatusAsync(repo.Path)).Branch);
            Assert.Equal("work", (await Git.GetStatusAsync(folder)).Branch);
        }
        finally
        {
            await Git.RunAsync(repo.Path, new[] { "worktree", "remove", "--force", folder });
        }
    }

    [Fact]
    public async Task AddWorktree_ExistingBranch_ChecksItOut()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        await repo.GitAsync("branch", "feature");
        var folder = Sibling(repo, "-feature");
        try
        {
            await Actions(repo.Path).AddWorktreeAsync(folder, "feature", "main");
            Assert.Equal("feature", (await Git.GetStatusAsync(folder)).Branch);
        }
        finally
        {
            await Git.RunAsync(repo.Path, new[] { "worktree", "remove", "--force", folder });
        }
    }

    [Fact]
    public async Task RemoveWorktree_Dirty_NeedsForce_AndKeepsBranch()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        var folder = Sibling(repo, "-wip");
        var a = Actions(repo.Path);
        await a.AddWorktreeAsync(folder, "wip", "main");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "changed");

        await Assert.ThrowsAsync<UserFacingException>(() => a.RemoveWorktreeAsync(folder, force: false));
        await a.RemoveWorktreeAsync(folder, force: true);

        Assert.False(Directory.Exists(folder));
        Assert.Single(await Git.GetWorktreesAsync(repo.Path));
        Assert.True(await Git.RefExistsAsync(repo.Path, "refs/heads/wip"));
    }

    [Fact]
    public async Task DeletedFolder_IsPrunable_ThenPruned()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        var folder = Sibling(repo, "-gone");
        await Actions(repo.Path).AddWorktreeAsync(folder, "gone", "main");
        Directory.Delete(folder, recursive: true);

        Assert.True((await Git.GetWorktreesAsync(repo.Path))[1].Prunable);
        await Actions(repo.Path).PruneWorktreesAsync();
        Assert.Single(await Git.GetWorktreesAsync(repo.Path));
    }

    [Theory]
    [InlineData("refs/heads/work", true)]
    [InlineData(@"refs\remotes\origin\main", true)]
    [InlineData("packed-refs", true)]
    [InlineData("HEAD", false)]
    [InlineData("index", false)]
    [InlineData("worktrees/x/HEAD", false)]
    [InlineData("refs/heads/work.lock", false)]
    public void MattersShared(string name, bool expected) => Assert.Equal(expected, RepoWatcher.MattersShared(name));
}
