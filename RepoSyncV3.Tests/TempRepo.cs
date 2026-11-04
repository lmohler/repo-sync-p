using System.IO;
using RepoSyncV3.Services;

namespace RepoSyncV3.Tests;

/// <summary>A throwaway git repo under %TEMP%, deleted on Dispose. Commits use a fixed test identity.</summary>
public sealed class TempRepo : IDisposable
{
    public string Path { get; }

    private TempRepo(string path) => Path = path;

    public static async Task<TempRepo> InitAsync(string branch = "main", bool bare = false)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repo-sync-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var repo = new TempRepo(dir);
        await repo.GitAsync(bare ? new[] { "init", "--bare", "-b", branch } : new[] { "init", "-b", branch });
        if (!bare)
        {
            await repo.GitAsync("config", "user.name", "Test User");
            await repo.GitAsync("config", "user.email", "test@example.com");
            await repo.GitAsync("config", "commit.gpgsign", "false");
        }
        return repo;
    }

    public async Task<string> GitAsync(params string[] args)
    {
        var r = await Git.RunAsync(Path, args);
        if (!r.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.CombinedOutput}");
        return r.StdOut;
    }

    public void Write(string file, string text)
    {
        var full = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public async Task CommitAsync(string file, string text, string message)
    {
        Write(file, text);
        await GitAsync("add", "--", file);
        await GitAsync("commit", "-m", message);
    }

    public void Dispose()
    {
        try
        {
            // git marks pack files read-only
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort: %TEMP% gets cleaned eventually
        }
    }
}
