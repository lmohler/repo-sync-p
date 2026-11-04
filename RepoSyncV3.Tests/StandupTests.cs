using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class StandupTests
{
    [Theory]
    [InlineData("2026-11-02", "2026-10-30")] // Monday -> Friday
    [InlineData("2026-11-04", "2026-11-03")] // Wednesday -> Tuesday
    [InlineData("2026-11-01", "2026-10-30")] // Sunday -> Friday
    public void LastWorkday_SkipsWeekends(string today, string expected) =>
        Assert.Equal(DateTime.Parse(expected), Standup.LastWorkday(DateTime.Parse(today)));

    [Fact]
    public async Task Collect_OnlyYourCommits_GroupedByRepo()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "a", "mine on main");
        await repo.GitAsync("switch", "-c", "feature/x");
        await repo.CommitAsync("b.txt", "b", "mine on a feature branch");
        await repo.GitAsync("-c", "user.email=someone@else.com", "commit", "--allow-empty", "-m", "not mine");

        var commits = await Standup.CollectAsync(new[] { ("demo", repo.Path, (IReadOnlyCollection<string>)new[] { "test@example.com" }) },
            DateTime.Today.AddDays(-1));
        var text = Standup.Format(commits, DateTime.Today.AddDays(-1));

        Assert.Equal(2, commits.Count);
        Assert.Contains("mine on a feature branch", text);
        Assert.DoesNotContain("not mine", text);
    }
}
