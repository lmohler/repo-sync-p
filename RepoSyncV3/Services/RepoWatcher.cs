using System.IO;
using System.Windows.Threading;

namespace RepoSyncV3.Services;

/// <summary>
/// Watches each repo's .git folder (HEAD, index, refs) so a commit, checkout or pull made outside the app -
/// VS Code, a terminal - shows on the card within a couple of seconds, even while this window is in the
/// background. Working-tree edits aren't watched (too many files); those still refresh when the window is activated.
/// </summary>
public sealed class RepoWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _changed;
    private readonly Dictionary<string, (List<FileSystemWatcher> Watchers, DispatcherTimer Timer)> _repos = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="changed">Called on the UI thread with the repo path, once per burst of changes.</param>
    public RepoWatcher(Dispatcher dispatcher, Action<string> changed)
    {
        _dispatcher = dispatcher;
        _changed = changed;
    }

    public void Watch(string repoPath)
    {
        if (_repos.ContainsKey(repoPath) || Git.GitDir(repoPath) is not { } gitDir || !Directory.Exists(gitDir)) return;

        var timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            _changed(repoPath);
        }, _dispatcher);
        timer.Stop();

        void Bump() => _dispatcher.BeginInvoke(() =>
        {
            timer.Stop();
            timer.Start();
        });

        if (Create(gitDir, e => { if (Matters(e.Name)) Bump(); }) is not { } watcher) return;
        var watchers = new List<FileSystemWatcher> { watcher };

        // A linked worktree's own git dir only has HEAD and index; branch refs live in the main repo's .git,
        // so a commit made in the worktree (or a fetch from either side) only shows up there.
        if (Git.CommonDir(repoPath) is { } common && !string.Equals(common, gitDir, StringComparison.OrdinalIgnoreCase)
            && Create(common, e => { if (MattersShared(e.Name)) Bump(); }) is { } shared)
            watchers.Add(shared);

        _repos[repoPath] = (watchers, timer);
    }

    private static FileSystemWatcher? Create(string dir, Action<FileSystemEventArgs> onEvent)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                InternalBufferSize = 16 * 1024,
            };
        }
        catch (Exception)
        {
            return null; // network drive or no access: the timer and window activation still refresh it
        }
        void OnEvent(object _, FileSystemEventArgs e) => onEvent(e);
        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public void Unwatch(string repoPath)
    {
        if (!_repos.Remove(repoPath, out var w)) return;
        w.Timer.Stop();
        foreach (var watcher in w.Watchers) watcher.Dispose();
    }

    /// <summary>What matters in the main repo's .git for a linked worktree: refs/ and packed-refs (shared by every
    /// worktree), not the main tree's own HEAD or index.</summary>
    public static bool MattersShared(string? relativeName)
    {
        if (string.IsNullOrEmpty(relativeName)) return false;
        var name = relativeName.Replace('\\', '/');
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        return name.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) || name == "packed-refs";
    }

    /// <summary>HEAD, index, refs/..., packed-refs, MERGE_HEAD and friends; not objects/, logs/ or lock files.</summary>
    public static bool Matters(string? relativeName)
    {
        if (string.IsNullOrEmpty(relativeName)) return false;
        var name = relativeName.Replace('\\', '/');
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("objects/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)) return true;
        return !name.Contains('/') && (name is "index" or "packed-refs" || name.EndsWith("HEAD", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var path in _repos.Keys.ToList()) Unwatch(path);
    }
}
