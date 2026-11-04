using System.IO;
using RepoSyncV3.Views;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Add SVN repo dialog: folder naming from the URL and the checks before setup runs.</summary>
public sealed class AddSvnRepoTests
{
    [Theory]
    [InlineData("https://svn.example.com/svn/web-portal/trunk", "web-portal")]
    [InlineData("https://svn.example.com/svn/web-portal/branches/feature-x/", "feature-x")]
    [InlineData("https://svn.example.com/svn/my%20repo/trunk", "my repo")]
    [InlineData("nonsense", null)]
    public void NameFromUrl(string url, string? expected) => Assert.Equal(expected, AddSvnRepoWindow.NameFromUrl(url));

    [Fact]
    public void Validate_RejectsWorkFolderInsideWorkingCopy()
    {
        var error = AddSvnRepoWindow.Validate("https://svn.example.com/svn/x/trunk", @"C:\code\x", "work", @"C:\code\x\work", "work", out _);
        Assert.Contains("outside the working copy", error);
    }

    [Fact]
    public void Validate_NeedsUrlUnlessFolderIsWorkingCopy()
    {
        var empty = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "nowc-" + Guid.NewGuid().ToString("N")[..8]);
        var error = AddSvnRepoWindow.Validate("", empty, "work", empty + "-work", "work", out _);
        Assert.Contains("isn't an SVN working copy", error);

        Assert.Null(AddSvnRepoWindow.Validate("https://svn.example.com/svn/x/trunk", empty, "work", empty + "-work", "work", out var request));
        Assert.Equal("https://svn.example.com/svn/x/trunk", request!.SvnUrl);
        Assert.Equal(empty + "-work", request.WorktreePath);
    }
}
