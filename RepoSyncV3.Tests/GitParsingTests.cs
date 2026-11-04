using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class GitParsingTests
{
    [Fact]
    public void ParseDiff_NumbersLinesFromHunkHeaders()
    {
        var diff = string.Join("\n",
            "diff --git a/a.txt b/a.txt",
            "index 1111111..2222222 100644",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -10,3 +10,3 @@ section",
            " same",
            "-old",
            "+new",
            " same");

        var lines = Git.ParseDiff(diff);

        var hunk = Assert.Single(lines, l => l.Kind == DiffKind.Hunk);
        Assert.StartsWith("@@ -10,3 +10,3 @@", hunk.Text);
        Assert.Equal("11", Assert.Single(lines, l => l.Kind == DiffKind.Delete).OldNo);
        Assert.Equal("11", Assert.Single(lines, l => l.Kind == DiffKind.Add).NewNo);
        Assert.Equal(2, lines.Count(l => l.Kind == DiffKind.Context));
    }

    [Fact]
    public async Task LocalBranches_MarkCurrentAndList()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        await repo.GitAsync("branch", "feature/one");
        await repo.GitAsync("checkout", "-b", "feature/two");

        var branches = await Git.GetLocalBranchesAsync(repo.Path);

        Assert.Equal(new[] { "feature/one", "feature/two", "main" }, branches.Select(b => b.Name).Order());
        Assert.Equal("feature/two", Assert.Single(branches, b => b.IsCurrent).Name);
    }

    [Fact]
    public async Task Log_ReturnsNewestFirstWithSubjects()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "1", "first commit");
        await repo.CommitAsync("a.txt", "2", "second: with | odd chars");

        var log = await Git.GetLogAsync(repo.Path, "HEAD", 10);

        Assert.Equal(new[] { "second: with | odd chars", "first commit" }, log.Select(c => c.Subject));
    }

    [Fact]
    public async Task Identity_ReportsLocalEmail()
    {
        using var repo = await TempRepo.InitAsync();

        var (name, email, local) = await Git.GetIdentityAsync(repo.Path);

        Assert.Equal("Test User", name);
        Assert.Equal("test@example.com", email);
        Assert.True(local);
    }

    [Fact]
    public async Task FindRepos_FindsNestedRepos()
    {
        using var outer = await TempRepo.InitAsync();
        var inner = Path.Combine(outer.Path, "sub", "inner");
        Directory.CreateDirectory(inner);
        await Git.RunAsync(inner, new[] { "init" });

        var found = Git.FindRepos(Path.Combine(outer.Path, "sub"));

        Assert.Contains(found, p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(inner), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SideBySide_AlignsAddsAndDeletes()
    {
        var diff = string.Join("\n",
            "@@ -1,3 +1,3 @@",
            " context",
            "-removed 1",
            "-removed 2",
            "+added 1",
            "+added 2",
            "+added 3",
            " trailing context");

        var unified = Git.ParseDiff(diff);
        var sbs = SideBySideDiffRow.FromUnified(unified);

        Assert.True(sbs[0].IsHeader);
        Assert.False(sbs[1].IsHeader);
        Assert.Equal(" context", sbs[1].LeftText);
        Assert.Equal(" context", sbs[1].RightText);

        Assert.Equal("-removed 1", sbs[2].LeftText);
        Assert.Equal("+added 1", sbs[2].RightText);
        Assert.Equal(DiffKind.Delete, sbs[2].LeftKind);
        Assert.Equal(DiffKind.Add, sbs[2].RightKind);

        Assert.Equal("-removed 2", sbs[3].LeftText);
        Assert.Equal("+added 2", sbs[3].RightText);

        Assert.False(sbs[4].HasLeft);
        Assert.Equal("+added 3", sbs[4].RightText);

        Assert.Equal(" trailing context", sbs[5].LeftText);
        Assert.Equal(" trailing context", sbs[5].RightText);
    }
}
