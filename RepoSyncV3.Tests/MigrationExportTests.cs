using System.IO;
using System.Text.Json;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class MigrationExportTests
{
    [Theory]
    [InlineData("https://oauth2:glpat-abc123@gitlab.example.com/g/p.git", "https://oauth2:***@gitlab.example.com/g/p.git", true)]
    [InlineData("https://ghp_0123456789abcdefghij@github.com/me/p.git", "https://***@github.com/me/p.git", true)]
    [InlineData("https://jdoe@gitlab.example.com/g/p.git", "https://jdoe@gitlab.example.com/g/p.git", false)]
    [InlineData("git@github.com:me/p.git", "git@github.com:me/p.git", false)]
    public void Redact_Masks_Credentials_In_Urls(string url, string expected, bool masked)
    {
        Assert.Equal(expected, MigrationExport.Redact(url, out var m));
        Assert.Equal(masked, m);
    }

    [Fact]
    public async Task Export_Lists_Remotes_And_Work_Not_On_The_Server()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "first");
        await repo.GitAsync("remote", "add", "origin", server.Path);
        await repo.GitAsync("push", "-u", "origin", "main");
        await repo.GitAsync("remote", "add", "mirror", "https://oauth2:glpat-secret@gitlab.example.com/g/p.git");

        await repo.CommitAsync("a.txt", "two", "unpushed");
        await repo.GitAsync("switch", "-c", "local-only");
        await repo.CommitAsync("b.txt", "b", "local work");
        repo.Write("b.txt", "stash me");
        await repo.GitAsync("stash");
        repo.Write("c.txt", "dirty");

        var settings = new AppSettings { Repos = { new RepoEntry { Path = repo.Path, DisplayName = "My repo", Pinned = true } } };
        var file = await MigrationExport.BuildAsync(settings);

        var r = Assert.Single(file.Repos);
        Assert.True(r.Exists);
        Assert.Equal("My repo", r.Name);
        Assert.True(r.Pinned);
        Assert.Equal(server.Path, r.CloneUrl);
        Assert.Equal("local-only", r.CurrentBranch);
        Assert.Equal("https://oauth2:***@gitlab.example.com/g/p.git", r.Remotes.Single(x => x.Name == "mirror").FetchUrl);
        Assert.Equal(1, r.Branches.Single(b => b.Name == "main").CommitsNotOnServer);
        Assert.Null(r.Branches.Single(b => b.Name == "local-only").Upstream);
        Assert.Equal(1, r.Stashes);
        Assert.Equal(1, r.UncommittedFiles);
        Assert.Equal("test@example.com", r.LocalIdentity?.Email);
        Assert.Contains(r.Warnings, w => w.Contains("'local-only' is local only"));
        Assert.Contains(r.Warnings, w => w.Contains("'main' has 1 unpushed"));
        Assert.Contains(r.Warnings, w => w.Contains("stash"));
        Assert.Contains(r.Warnings, w => w.Contains("uncommitted"));
        Assert.Contains(r.Warnings, w => w.Contains("masked"));
        Assert.Empty(file.Settings.Repos);

        var path = Path.Combine(Path.GetTempPath(), "repo-sync-tests", $"export-{Guid.NewGuid():N}.json");
        try
        {
            await MigrationExport.WriteAsync(file, path);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("glpat-secret", json);
            Assert.NotNull(JsonSerializer.Deserialize<MigrationFile>(json));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_Flags_Missing_Folder()
    {
        var settings = new AppSettings { Repos = { new RepoEntry { Path = @"C:\does-not-exist\nowhere" } } };
        var file = await MigrationExport.BuildAsync(settings);
        var r = Assert.Single(file.Repos);
        Assert.False(r.Exists);
        Assert.NotEmpty(r.Warnings);
    }
}
