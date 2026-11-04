using System.Collections.ObjectModel;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

/// <summary>One line on the home page: which repo, what about it, and where clicking it goes.</summary>
/// <param name="Tone">Pill color: success, info, warning, error, purple.</param>
/// <param name="Tab">The repo tab opened on click.</param>
public sealed record DashboardItem(RepoViewModel Repo, string Title, string Detail, string Badge, string Tone, int Tab, string? Url = null)
{
    public string RepoName => Repo.Name;
    public string WorkspaceName => Repo.WorkspaceName;
    public System.Windows.Media.SolidColorBrush WorkspaceBrush => Repo.WorkspaceBrush;
    public System.Windows.Media.SolidColorBrush WorkspaceTint => Repo.WorkspaceTint;
}

/// <summary>Your commits in one repo per day (oldest first), drawn as a row of bars.</summary>
public sealed record ActivityRow(RepoViewModel Repo, int[] Days)
{
    public string RepoName => Repo.Name;
    public int Total => Days.Sum();
    /// <summary>Bar heights 2..22 px, scaled to this row's busiest day.</summary>
    public IReadOnlyList<ActivityBar> Bars => Days.Select((n, i) => new ActivityBar(n == 0 ? 2 : 4 + 18.0 * n / Math.Max(1, Days.Max()),
        n, DateTime.Today.AddDays(i - Days.Length + 1))).ToList();
}

public sealed record ActivityBar(double Height, int Count, DateTime Day)
{
    public string Tip => $"{Day:ddd MMM d}: {Count} commit(s)";
    public bool Any => Count > 0;
}

/// <summary>
/// The home page (shown when no repo is selected): across every repo in the current workspace - what needs you
/// locally, failing / running checks, your open merge requests / pull requests, and the ones waiting for your
/// review. Server lists are read once per project (clones of the same project are asked once).
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private List<DashboardItem> _mine = new();
    private List<DashboardItem> _review = new();
    private int _loadVersion;

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(LoadAsync);
        OpenItemCommand = new RelayCommand(p =>
        {
            if (p is DashboardItem item) _main.OpenRepo(item.Repo, item.Tab);
        });
        OpenItemWebCommand = new RelayCommand(p =>
        {
            if (p is DashboardItem { Url: { } url }) Shell.OpenUrl(url);
        }, p => p is DashboardItem { Url: not null });
    }

    public ObservableCollection<DashboardItem> Attention { get; } = new();
    public ObservableCollection<DashboardItem> Checks { get; } = new();
    public ObservableCollection<DashboardItem> MyMergeRequests { get; } = new();
    public ObservableCollection<DashboardItem> ReviewRequests { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand OpenItemCommand { get; }
    public ICommand OpenItemWebCommand { get; }

    private bool _loading;
    private string _filter = string.Empty;
    private string? _serverNote;
    private DateTime? _loadedAt;

    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    /// <summary>Filters every list by repo, title, branch or detail.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) Apply();
        }
    }

    /// <summary>Servers without a token, or that failed to answer.</summary>
    public string? ServerNote { get => _serverNote; private set => Set(ref _serverNote, value); }

    public string LoadedText => _loadedAt is null ? string.Empty : "updated " + TimeText.Relative(_loadedAt);

    public bool AllClear => Attention.Count + Checks.Count + MyMergeRequests.Count + ReviewRequests.Count == 0 && !Loading;

    private List<DashboardItem> _attention = new();
    private List<DashboardItem> _checks = new();

    /// <summary>Local state only (cheap): needs-you and checks lists from what the repos already know.</summary>
    public void RebuildLocal()
    {
        var repos = _main.ScopeRepos;
        _attention = repos
            .Where(r => r.Loaded && (r.NeedsAttention || r.IsDirty || r.Ahead > 0 || r.State == RepoState.Unpublished))
            .OrderByDescending(r => r.NeedsAttention).ThenBy(r => r.Name)
            .Select(r => new DashboardItem(r, r.Next.Title, $"{r.Branch}  ·  {r.StateLabel}", r.Next.Tone == "error" ? "needs you" : r.StateLabel, r.Next.Tone,
                RepoDetailViewModel.TabOverview))
            .ToList();
        _checks = repos
            .Where(r => r.Pipeline is { Kind: "failed" or "running" or "pending" })
            .OrderBy(r => r.PipelineKind == "failed" ? 0 : 1).ThenBy(r => r.Name)
            .Select(r => new DashboardItem(r, $"{r.Branch}: {r.PipelineLabel}", r.Pipeline!.Name ?? $"#{r.Pipeline.Id} · {r.Pipeline.ShortSha}",
                r.Pipeline.Label, r.PipelineKind == "failed" ? "error" : "info", RepoDetailViewModel.TabPipelines, r.Pipeline.WebUrl))
            .ToList();
        Apply();
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        RebuildLocal();
        var activity = Task.WhenAll(LoadActivityAsync(), LoadRemindersAsync());
        Loading = true;
        OnPropertyChanged(nameof(AllClear));
        var notes = new List<string>();
        var mine = new List<DashboardItem>();
        var review = new List<DashboardItem>();
        try
        {
            // one repo per project, so two clones of the same project aren't asked twice
            var projects = _main.ScopeRepos.Where(r => r.Remote is not null)
                .GroupBy(r => r.Remote!.WebUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var missingTokens = projects.Where(r => !r.HasToken).Select(r => r.Remote!.Host).Distinct().ToList();
            if (missingTokens.Count > 0) notes.Add($"No token for {string.Join(", ", missingTokens)} - add one under Accounts (key button).");

            using var gate = new SemaphoreSlim(6);
            var failed = 0;
            await Task.WhenAll(projects.Where(r => r.HasToken).Select(async repo =>
            {
                await gate.WaitAsync();
                try
                {
                    var client = Forges.For(repo.Remote!)!;
                    var m = await client.ListMergeRequestsAsync(repo.Remote!, MergeRequestScope.Mine);
                    var rv = await client.ListMergeRequestsAsync(repo.Remote!, MergeRequestScope.ToReview);
                    lock (mine) mine.AddRange(m.Select(x => ToItem(repo, x)));
                    lock (review) review.AddRange(rv.Select(x => WaitingDays(x) >= 3
                        ? ToItem(repo, x) with { Badge = $"waiting {WaitingDays(x)}d", Tone = "warning" }
                        : ToItem(repo, x)));
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    gate.Release();
                }
            }));
            if (failed > 0) notes.Add($"{failed} project(s) didn't answer - see each repo's tabs for the error.");
            await activity;
        }
        finally
        {
            if (version == _loadVersion)
            {
                _mine = mine.OrderByDescending(i => i.Badge == "ready").ThenBy(i => i.RepoName).ToList();
                _review = review.OrderByDescending(i => i.Badge.StartsWith("waiting", StringComparison.Ordinal)).ThenBy(i => i.RepoName).ToList();
                ServerNote = notes.Count == 0 ? null : string.Join("\n", notes);
                _loadedAt = DateTime.Now;
                Loading = false;
                Apply();
                OnPropertyChanged(nameof(LoadedText));
            }
        }
    }

    // ---- tidy-up reminders: old stashes, branches whose server branch is gone ----

    public const int OldStashDays = 14;
    public ObservableCollection<DashboardItem> Reminders { get; } = new();

    public async Task LoadRemindersAsync()
    {
        var items = new List<DashboardItem>();
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(_main.ScopeRepos.Where(r => r.Loaded && System.IO.Directory.Exists(r.Path)).Select(async repo =>
        {
            await gate.WaitAsync();
            try
            {
                var cutoff = DateTimeOffset.Now.AddDays(-OldStashDays).ToUnixTimeSeconds();
                var stashes = (await Git.RunAsync(repo.Path, new[] { "stash", "list", "--format=%ct" })).StdOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => long.TryParse(l.Trim(), out var t) && t < cutoff);
                if (stashes > 0)
                    lock (items) items.Add(new DashboardItem(repo, $"{stashes} stash(es) older than {OldStashDays} days",
                        "Apply what you still need, delete the rest.", "old stash", "warning", RepoDetailViewModel.TabStashes));

                var gone = (await Git.RunAsync(repo.Path, new[] { "for-each-ref", "refs/heads", "--format=%(refname:short)\x1f%(upstream:track)" })).StdOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Split('\x1f')).Where(f => f.Length == 2 && f[1].Contains("gone")).Select(f => f[0]).ToList();
                if (gone.Count > 0)
                    lock (items) items.Add(new DashboardItem(repo, $"{gone.Count} branch(es) deleted on the server",
                        string.Join(", ", gone.Take(4)) + (gone.Count > 4 ? ", ..." : string.Empty) + "  ·  Clean up on the Branches tab", "gone", "purple",
                        RepoDetailViewModel.TabBranches));
            }
            finally
            {
                gate.Release();
            }
        }));
        _reminders = items.OrderBy(i => i.RepoName).ToList();
        Apply();
    }

    private List<DashboardItem> _reminders = new();

    // ---- activity: your commits per repo, last 14 days (local git, every branch) ----

    public const int ActivityDays = 14;
    public ObservableCollection<ActivityRow> Activity { get; } = new();
    private int _activityTotal;
    public int ActivityTotal { get => _activityTotal; private set => Set(ref _activityTotal, value); }

    public async Task LoadActivityAsync()
    {
        var since = DateTime.Today.AddDays(-(ActivityDays - 1));
        var rows = new List<ActivityRow>();
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(_main.ScopeRepos.Where(r => r.Loaded && r.GitEmail.Length > 0).Select(async repo =>
        {
            await gate.WaitAsync();
            try
            {
                var r = await Git.RunAsync(repo.Path, new[] { "log", "--all", $"--since={since:yyyy-MM-dd}", $"--author={repo.GitEmail}", "--format=%ad", "--date=short" });
                var days = new int[ActivityDays];
                foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (DateTime.TryParse(line.Trim(), out var d) && (d - since).Days is >= 0 and < ActivityDays) days[(d - since).Days]++;
                if (days.Sum() > 0) lock (rows) rows.Add(new ActivityRow(repo, days));
            }
            finally
            {
                gate.Release();
            }
        }));
        Replace(Activity, rows.OrderByDescending(r => r.Total).Take(12));
        ActivityTotal = rows.Sum(r => r.Total);
    }

    /// <summary>Days since a review request last changed - how long it has been waiting on you.</summary>
    private static int WaitingDays(ForgeMergeRequest mr) => mr.UpdatedAt is { } u ? (int)(DateTime.Now - u.ToLocalTime()).TotalDays : 0;

    private static DashboardItem ToItem(RepoViewModel repo, ForgeMergeRequest mr) =>
        new(repo, $"{mr.Reference}  {mr.Title}", $"{mr.Branches}  ·  {mr.AuthorName}  ·  updated {TimeText.Relative(mr.UpdatedAt)}",
            mr.Readiness, mr.Kind switch { "ready" => "success", "conflicts" => "error", "draft" => "", _ => "info" },
            RepoDetailViewModel.TabMergeRequests, mr.WebUrl);

    private void Apply()
    {
        bool Match(DashboardItem i) => _filter.Length == 0
            || i.RepoName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || i.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || i.Detail.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        Replace(Attention, _attention.Where(Match));
        Replace(Checks, _checks.Where(Match));
        Replace(MyMergeRequests, _mine.Where(Match));
        Replace(ReviewRequests, _review.Where(Match));
        Replace(Reminders, _reminders.Where(Match));
        OnPropertyChanged(nameof(AllClear));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
