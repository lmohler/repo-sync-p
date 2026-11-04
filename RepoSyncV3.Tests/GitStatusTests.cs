using System.IO;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Git.GetStatusAsync against real repos: the state every card and the Next step banner are built from.</summary>
public class GitStatusTests
{
    [Fact]
    public async Task CleanRepo_IsNotDirty()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.True(s.Ok);
        Assert.Equal("main", s.Branch);
        Assert.False(s.IsDirty);
        Assert.Null(s.Upstream);
    }

    [Fact]
    public async Task NewRepo_ReportsBranchBeforeFirstCommit()
    {
        using var repo = await TempRepo.InitAsync("develop");

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.Equal("develop", s.Branch);
    }

    [Fact]
    public async Task StagedUnstagedAndUntracked_AreSeparated()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        await repo.CommitAsync("b.txt", "one", "second");

        repo.Write("a.txt", "changed");           // unstaged modify
        repo.Write("b.txt", "changed");
        await repo.GitAsync("add", "b.txt");      // staged modify
        repo.Write("new folder/c d.txt", "x");    // untracked, with spaces

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.Equal("a.txt", Assert.Single(s.Unstaged, f => f.Code == 'M').Path);
        Assert.Equal("b.txt", Assert.Single(s.Staged).Path);
        Assert.Equal("new folder/c d.txt", Assert.Single(s.Unstaged, f => f.Code == '?').Path);
    }

    [Fact]
    public async Task NonAsciiFileName_ComesBackAsIs()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        repo.Write("café ü.txt", "x");

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.Equal("café ü.txt", Assert.Single(s.Unstaged).Path);
    }

    [Fact]
    public async Task Rename_KeepsOriginalPath()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("old.txt", "some content that stays the same", "first");
        await repo.GitAsync("mv", "old.txt", "new.txt");

        var s = await Git.GetStatusAsync(repo.Path);

        var f = Assert.Single(s.Staged);
        Assert.Equal('R', f.Code);
        Assert.Equal("new.txt", f.Path);
        Assert.Equal("old.txt", f.OrigPath);
    }

    [Fact]
    public async Task AheadAndBehind_AreReadFromTheBranchLine()
    {
        using var remote = await TempRepo.InitAsync(bare: true);
        using var mine = await TempRepo.InitAsync();
        await mine.CommitAsync("a.txt", "one", "first");
        await mine.GitAsync("remote", "add", "origin", remote.Path);
        await mine.GitAsync("push", "-u", "origin", "main");

        // someone else pushes one commit
        using var theirs = await TempRepo.InitAsync();
        await theirs.GitAsync("remote", "add", "origin", remote.Path);
        await theirs.GitAsync("pull", "origin", "main");
        await theirs.CommitAsync("b.txt", "two", "theirs");
        await theirs.GitAsync("push", "origin", "HEAD:main");

        // and I make two
        await mine.CommitAsync("c.txt", "three", "mine 1");
        await mine.CommitAsync("d.txt", "four", "mine 2");
        await mine.GitAsync("fetch");

        var s = await Git.GetStatusAsync(mine.Path);

        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
    }

    [Fact]
    public async Task DeletedUpstream_IsGone()
    {
        using var remote = await TempRepo.InitAsync(bare: true);
        using var mine = await TempRepo.InitAsync();
        await mine.CommitAsync("a.txt", "one", "first");
        await mine.GitAsync("remote", "add", "origin", remote.Path);
        await mine.GitAsync("push", "origin", "main");
        await mine.GitAsync("checkout", "-b", "feature/x");
        await mine.GitAsync("push", "-u", "origin", "feature/x");
        await mine.GitAsync("push", "origin", "--delete", "feature/x");
        await mine.GitAsync("fetch", "--prune");

        var s = await Git.GetStatusAsync(mine.Path);

        Assert.Equal("feature/x", s.Branch);
        Assert.True(s.UpstreamGone);
    }

    [Fact]
    public async Task DetachedHead_IsFlagged()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        await repo.GitAsync("checkout", "--detach");

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.True(s.Detached);
    }

    [Fact]
    public async Task MergeConflict_IsReported()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "base", "base");
        await repo.GitAsync("checkout", "-b", "other");
        await repo.CommitAsync("a.txt", "other", "other");
        await repo.GitAsync("checkout", "main");
        await repo.CommitAsync("a.txt", "main", "main");
        await Git.RunAsync(repo.Path, new[] { "merge", "other" }); // fails with a conflict, on purpose

        var s = await Git.GetStatusAsync(repo.Path);

        Assert.Equal("a.txt", Assert.Single(s.Conflicted).Path);
        Assert.True(Git.IsMergeInProgress(repo.Path));
    }

    [Fact]
    public async Task NotARepo_IsNotOk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "not-a-repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var s = await Git.GetStatusAsync(dir);
            Assert.False(s.Ok);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
