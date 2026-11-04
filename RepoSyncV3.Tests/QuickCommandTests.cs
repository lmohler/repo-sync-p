using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>The quick git bar asks before running these.</summary>
public class QuickCommandTests
{
    private static string? Reason(string commandLine) => RepoActions.DestructiveReason(RepoActions.ParseArguments(commandLine));

    [Theory]
    [InlineData("reset --hard")]
    [InlineData("reset --hard origin/main")]
    [InlineData("clean -fdx")]
    [InlineData("clean --force")]
    [InlineData("push --force")]
    [InlineData("push -f origin main")]
    [InlineData("push --force-with-lease")]
    [InlineData("push origin --delete feature/x")]
    [InlineData("push origin :feature/x")]
    [InlineData("push origin +main")]
    [InlineData("branch -D feature/x")]
    [InlineData("checkout -- .")]
    [InlineData("checkout .")]
    [InlineData("restore src/a.cs")]
    [InlineData("stash drop")]
    [InlineData("stash clear")]
    [InlineData("tag -d v1.0")]
    [InlineData("rebase -i HEAD~3")]
    [InlineData("rm a.cs")]
    public void Destructive_Asks(string cmd) => Assert.NotNull(Reason(cmd));

    [Theory]
    [InlineData("status")]
    [InlineData("log --oneline -5")]
    [InlineData("reset HEAD~1")]
    [InlineData("reset --soft HEAD~1")]
    [InlineData("clean -n")]
    [InlineData("push")]
    [InlineData("push -u origin feature/x")]
    [InlineData("branch -d feature/x")]
    [InlineData("checkout main")]
    [InlineData("restore --staged a.cs")]
    [InlineData("stash")]
    [InlineData("stash list")]
    [InlineData("tag -l")]
    [InlineData("remote -v")]
    [InlineData("rm --cached secrets.json")]
    public void Safe_RunsStraightAway(string cmd) => Assert.Null(Reason(cmd));

    [Fact]
    public void ParseArguments_KeepsQuotedText()
    {
        var args = RepoActions.ParseArguments("commit -m \"fix the thing\" --no-verify");

        Assert.Equal(new[] { "commit", "-m", "fix the thing", "--no-verify" }, args);
    }
}
