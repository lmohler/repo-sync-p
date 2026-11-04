using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Profiles: settings from before profiles get an explicit profile per repo (the old server rule, once),
/// and broken settings are made consistent.</summary>
public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rsv3-profiles-" + Guid.NewGuid().ToString("N"));

    public ProfileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A folder with just enough .git to have a remote URL (no git process needed).</summary>
    private string FakeRepo(string name, string? url, string remote = "origin")
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        var config = "[core]\n\tbare = false\n" + (url is null ? "" : $"[remote \"{remote}\"]\n\turl = {url}\n\tfetch = +refs/heads/*:refs/remotes/{remote}/*\n");
        File.WriteAllText(Path.Combine(path, ".git", "config"), config);
        return path;
    }

    [Fact]
    public void OldSettings_GetTheServerRuleOnce()
    {
        var settings = new AppSettings { DefaultWorkspace = WorkspaceSettings.WorkId, AutoFetchMinutes = 7, CloneRoot = @"C:\code" };
        settings.Workspaces[0].Hosts.Add("gitlab.com");
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("gh", "https://github.com/me/gh.git") });
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("corp", "git@gitlab.example.com:team/corp.git", remote: "gitlab") });
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("listed", "https://gitlab.com/me/listed.git") });
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("local", null) });
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("chosen", "https://github.com/me/chosen.git"), Workspace = WorkspaceSettings.WorkId });

        Workspaces.EnsureDefaults(settings);

        Assert.Equal(new[] { "personal", "work", "personal", "work", "work" }, settings.Repos.Select(e => e.Workspace).ToArray());
        Assert.All(settings.Workspaces, w => Assert.Equal(7, w.AutoFetchMinutes));
        Assert.All(settings.Workspaces, w => Assert.Equal(@"C:\code", w.CloneRoot));
        Assert.All(settings.Workspaces, w => Assert.Empty(w.Hosts));
    }

    [Fact]
    public void BrokenSettings_AreMadeConsistent()
    {
        var settings = new AppSettings
        {
            Workspaces = new()
            {
                new WorkspaceSettings { Id = "school", Name = "School", AutoFetchMinutes = 0 },
                new WorkspaceSettings { Id = "school", Name = "" },
            },
            ActiveWorkspace = "gone",
            DefaultWorkspace = "gone",
        };
        settings.Repos.Add(new RepoEntry { Path = FakeRepo("orphan", null), Workspace = "deleted-profile" });

        Workspaces.EnsureDefaults(settings);

        Assert.Equal(2, settings.Workspaces.Select(w => w.Id).Distinct().Count());
        Assert.Equal("Profile", settings.Workspaces[1].Name);
        Assert.Equal(0, settings.Workspaces[0].AutoFetchMinutes);
        Assert.Equal("school", settings.Repos[0].Workspace);
        Assert.Equal("school", settings.ActiveWorkspace);
    }

    [Fact]
    public void NoProfiles_GetsTheDefaults_AndAllStaysAll()
    {
        var settings = new AppSettings { Workspaces = new(), ActiveWorkspace = Workspaces.AllId };
        Workspaces.EnsureDefaults(settings);
        Assert.Equal(new[] { "personal", "work" }, settings.Workspaces.Select(w => w.Id).ToArray());
        Assert.Equal(Workspaces.AllId, settings.ActiveWorkspace);
    }
}
