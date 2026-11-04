using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class JobLogsTests
{
    [Fact]
    public void Clean_StripsAnsiSectionsAndProgress()
    {
        var raw = "\x1B[0Ksection_start:1700000000:build\r\x1B[0K\x1B[32;1mBuilding\x1B[0;m\r\nDownloading 10%\rDownloading 100%\n";
        Assert.Equal("Building\nDownloading 100%\n", JobLogs.Clean(raw));
    }

    [Fact]
    public void FirstErrorLine_SkipsSummaryLines()
    {
        var log = string.Join('\n',
            "Restoring packages",
            "Build succeeded. 0 Error(s)",
            "Warning: something deprecated",
            "src/Foo.cs(12,5): error CS1002: ; expected",
            "Job failed: exit code 1");
        Assert.Equal(3, JobLogs.FirstErrorLine(log));
        Assert.Equal(-1, JobLogs.FirstErrorLine("all good\nPassed: 12, Failed: 0"));
    }
}
