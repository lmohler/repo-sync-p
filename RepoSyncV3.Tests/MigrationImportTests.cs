using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class MigrationImportTests
{
    [Theory]
    [InlineData("https://oauth2:***@gitlab.example.com/g/p.git", "https://oauth2@gitlab.example.com/g/p.git")]
    [InlineData("https://***@github.com/me/p.git", "https://github.com/me/p.git")]
    [InlineData("git@github.com:me/p.git", "git@github.com:me/p.git")]
    public void Unmask_Drops_Masked_Credentials(string url, string expected) =>
        Assert.Equal(expected, MigrationImport.Unmask(url));

    [Fact]
    public void MapPath_Moves_Profile_Paths_To_This_User()
    {
        var inProfile = new MigrationRepo { Path = @"C:\Users\old\code\app", PathInProfile = @"%USERPROFILE%\code\app" };
        var elsewhere = new MigrationRepo { Path = @"D:\src\lib" };
        Assert.Equal(@"C:\Users\new\code\app", MigrationImport.MapPath(inProfile, @"C:\Users\new"));
        Assert.Equal(@"D:\src\lib", MigrationImport.MapPath(elsewhere, @"C:\Users\new"));
    }

    [Fact]
    public void Settings_Diff_Lists_Only_Differences_And_Applies_Only_Ticked_Rows()
    {
        var mine = new AppSettings { BranchPattern = "feature/{name}", EditorCommand = "code", WindowWidth = 100 };
        mine.Workspaces[1].CloneRoot = @"C:\Users\new\work";
        mine.Workspaces[1].GitEmail = "me@work.example";

        var theirs = new AppSettings { BranchPattern = "topic/{name}", EditorCommand = "rider64", WindowWidth = 2000 };
        theirs.Workspaces[1].CloneRoot = @"C:\Users\old\work"; // same place once the profile is mapped
        theirs.Workspaces[1].GitEmail = "other@work.example";
        var file = new MigrationFile { UserProfile = @"C:\Users\old", Settings = theirs };

        var diff = MigrationImport.DiffSettings(mine, file, @"C:\Users\new");
        Assert.Equal(new[] { "BranchPattern", "EditorCommand", "Workspaces[work].GitEmail" }, diff.Select(d => d.Key).OrderBy(k => k).ToArray());
        Assert.All(diff, d => Assert.False(d.Take)); // nothing is overwritten unless ticked

        diff.Single(d => d.Key == "BranchPattern").Take = true;
        diff.Single(d => d.Key == "Workspaces[work].GitEmail").Take = true;
        MigrationImport.ApplySettings(mine, diff);

        Assert.Equal("topic/{name}", mine.BranchPattern);
        Assert.Equal("code", mine.EditorCommand);
        Assert.Equal("other@work.example", mine.Workspaces.Single(w => w.Id == "work").GitEmail);
        Assert.Equal(@"C:\Users\new\work", mine.Workspaces.Single(w => w.Id == "work").CloneRoot);
        Assert.Equal(100, mine.WindowWidth);
    }

    [Fact]
    public void Git_Config_Diff_Marks_Secrets_And_Local_Only_Keys()
    {
        var mine = new Dictionary<string, List<string>>
        {
            ["user.name"] = new() { "Me" },
            ["core.autocrlf"] = new() { "true" },
            ["gui.recentrepo"] = new() { "x" },
        };
        var theirs = new Dictionary<string, List<string>>
        {
            ["user.name"] = new() { "Me Too" },
            ["core.autocrlf"] = new() { "true" },
            ["http.extraheader"] = new() { "***" },
            ["safe.directory"] = new() { "a", "b" },
        };
        var diff = MigrationImport.DiffGitConfig(mine, theirs);

        Assert.DoesNotContain(diff, d => d.Key == "core.autocrlf");
        Assert.True(diff.Single(d => d.Key == "user.name").CanTake);
        Assert.False(diff.Single(d => d.Key == "http.extraheader").CanTake);
        Assert.False(diff.Single(d => d.Key == "gui.recentrepo").CanTake);
        Assert.Equal("a\nb", diff.Single(d => d.Key == "safe.directory").Theirs);
    }

    [Fact]
    public async Task Export_Then_Import_Clones_Missing_Repo_With_Branch_Remotes_And_Identity()
    {
        using var server = await TempRepo.InitAsync(bare: true);
        using var old = await TempRepo.InitAsync();
        await old.CommitAsync("a.txt", "one", "first");
        await old.GitAsync("remote", "add", "origin", server.Path);
        await old.GitAsync("push", "-u", "origin", "main");
        await old.GitAsync("switch", "-c", "feature/x");
        await old.CommitAsync("b.txt", "b", "feature");
        await old.GitAsync("push", "-u", "origin", "feature/x");
        await old.GitAsync("remote", "add", "upstream", "https://example.com/up.git");

        using var here = await TempRepo.InitAsync(); // a repo that is already on the "new" computer
        var file = await MigrationExport.BuildAsync(new AppSettings
        {
            Repos = { new RepoEntry { Path = old.Path, Pinned = true }, new RepoEntry { Path = here.Path } },
        });

        var target = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "import-" + Guid.NewGuid().ToString("N")[..8]);
        var plan = MigrationImport.PlanRepos(file, _ => false, MigrationImport.Home);
        var cloneItem = plan.Single(p => p.Source.Path == old.Path);
        var existingItem = plan.Single(p => p.Source.Path == here.Path);
        Assert.Equal(ImportAction.AddExisting, existingItem.Action);
        Assert.True(existingItem.Selected);

        cloneItem.Target = old.Path;
        Assert.Equal(ImportAction.AddExisting, cloneItem.Action); // folder already there
        cloneItem.Target = target;
        Assert.Equal(ImportAction.Clone, cloneItem.Action);
        Assert.True(cloneItem.Selected);

        Assert.Equal(ImportAction.Skip, MigrationImport.PlanRepos(file, p => p == here.Path, MigrationImport.Home)
            .Single(p => p.Source.Path == here.Path).Action);

        try
        {
            var log = new Progress<LogEntry>(_ => { });
            await RepoActions.CloneAsync(cloneItem.CloneUrl!, target, log, CancellationToken.None);
            await MigrationImport.FinishCloneAsync(cloneItem, log);

            Assert.Equal("feature/x", (await Git.RunAsync(target, new[] { "branch", "--show-current" })).StdOut.Trim());
            Assert.Equal("https://example.com/up.git", (await Git.RunAsync(target, new[] { "remote", "get-url", "upstream" })).StdOut.Trim());
            Assert.Equal("test@example.com", (await Git.RunAsync(target, new[] { "config", "--local", "user.email" })).StdOut.Trim());
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(target, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
