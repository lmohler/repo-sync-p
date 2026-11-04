using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class RepoSearchTests
{
    [Fact]
    public async Task Search_FindsAcrossRepos_WithLineNumbers()
    {
        using var a = await TempRepo.InitAsync();
        using var b = await TempRepo.InitAsync();
        await a.CommitAsync("src/one.cs", "class One\n{\n    // TODO: needle here\n}\n", "a");
        await b.CommitAsync("two.txt", "Needle at top\n", "b");

        var caseless = await RepoSearch.SearchAsync(new[] { ("a", a.Path), ("b", b.Path) }, "needle", regex: false, matchCase: false);
        var exact = await RepoSearch.SearchAsync(new[] { ("a", a.Path), ("b", b.Path) }, "needle", regex: false, matchCase: true);

        Assert.Equal(2, caseless.Count);
        var hit = Assert.Single(exact);
        Assert.Equal(("a", "src/one.cs", 3), (hit.Repo, hit.File, hit.Line));
    }
}
