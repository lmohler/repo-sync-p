using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class RepoWatcherTests
{
    [Theory]
    [InlineData("HEAD")]
    [InlineData("index")]
    [InlineData("ORIG_HEAD")]
    [InlineData("MERGE_HEAD")]
    [InlineData("FETCH_HEAD")]
    [InlineData("packed-refs")]
    [InlineData(@"refs\heads\main")]
    [InlineData("refs/remotes/origin/feature/x")]
    public void Matters(string name) => Assert.True(RepoWatcher.Matters(name));

    [Theory]
    [InlineData(null)]
    [InlineData("index.lock")]
    [InlineData(@"refs\heads\main.lock")]
    [InlineData(@"objects\ab\cdef")]
    [InlineData("logs/HEAD")]
    [InlineData("config")]
    [InlineData("COMMIT_EDITMSG")]
    public void Ignored(string? name) => Assert.False(RepoWatcher.Matters(name));
}
