namespace RepoSyncV3.Services;

public sealed record SearchHit(string Repo, string RepoPath, string File, int Line, string Text)
{
    public string FullPath => System.IO.Path.Combine(RepoPath, File.Replace('/', '\\'));
}

/// <summary>git grep across many repos at once (tracked files in the working tree, text files only).</summary>
public static class RepoSearch
{
    public const int MaxPerRepo = 200;

    public static async Task<List<SearchHit>> SearchAsync(IEnumerable<(string Name, string Path)> repos, string query, bool regex, bool matchCase,
        CancellationToken ct = default)
    {
        if (query.Length == 0) return new List<SearchHit>();
        using var gate = new SemaphoreSlim(6);
        var tasks = repos.Where(r => System.IO.Directory.Exists(r.Path)).Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var args = new List<string> { "grep", "-n", "-I", "-z", "--no-color", $"--max-count={MaxPerRepo}", regex ? "-E" : "-F" };
                if (!matchCase) args.Add("-i");
                args.Add("-e");
                args.Add(query);
                var result = await Git.RunAsync(r.Path, args, ct);
                return Parse(r.Name, r.Path, result.StdOut).Take(MaxPerRepo).ToList();
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(tasks)).SelectMany(h => h).ToList();
    }

    /// <summary>Parses "file\0line\0text\n" lines from git grep -n -z.</summary>
    public static IEnumerable<SearchHit> Parse(string repo, string repoPath, string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\0', 3);
            if (f.Length == 3 && int.TryParse(f[1], out var n))
                yield return new SearchHit(repo, repoPath, f[0], n, f[2].TrimEnd('\r').Trim());
        }
    }
}
