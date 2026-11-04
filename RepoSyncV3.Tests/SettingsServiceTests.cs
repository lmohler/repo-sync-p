using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>settings.json lives next to the exe - for tests, the test output folder.</summary>
public class SettingsServiceTests : IDisposable
{
    private static readonly string SettingsFile = Path.Combine(SettingsService.SettingsDir, "settings.json");

    public SettingsServiceTests() => Cleanup();

    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        foreach (var f in Directory.GetFiles(SettingsService.SettingsDir, "settings.json*")) File.Delete(f);
    }

    private static AppSettings WithRepo(string path)
    {
        var s = new AppSettings();
        s.Repos.Add(new RepoEntry { Path = path });
        return s;
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        Assert.Null(SettingsService.Save(WithRepo(@"C:\src\one")));

        var loaded = SettingsService.Load();

        Assert.Equal(@"C:\src\one", Assert.Single(loaded.Repos).Path);
    }

    [Fact]
    public void SecondSave_KeepsPreviousAsBackup()
    {
        SettingsService.Save(WithRepo(@"C:\src\one"));
        SettingsService.Save(WithRepo(@"C:\src\two"));

        Assert.Contains(@"C:\\src\\one", File.ReadAllText(SettingsFile + ".bak"));
        Assert.Contains(@"C:\\src\\two", File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".tmp"));
    }

    [Fact]
    public void CorruptFile_FallsBackToBackupAndIsKeptAside()
    {
        SettingsService.Save(WithRepo(@"C:\src\one"));
        SettingsService.Save(WithRepo(@"C:\src\two"));
        File.WriteAllText(SettingsFile, "{ this is not json");

        var loaded = SettingsService.Load();

        Assert.Equal(@"C:\src\one", Assert.Single(loaded.Repos).Path);
        var aside = Assert.Single(Directory.GetFiles(SettingsService.SettingsDir, "settings.json.corrupt-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(aside));
        Assert.NotNull(SettingsService.LoadProblem);
    }
}
