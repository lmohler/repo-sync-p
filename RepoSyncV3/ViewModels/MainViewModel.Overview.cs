using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

public sealed partial class MainViewModel
{
    // =================================================================================== workspace overview + change feed

    /// <summary>Every repo in the current workspace as one sortable, groupable table (its own view of <see cref="Repos"/>,
    /// so sorting here doesn't reorder the sidebar).</summary>
    public ICollectionView OverviewView { get; private set; } = null!;

    /// <summary>What background (and manual) fetches found: new commits, finished pipelines, merge request news. Newest first.</summary>
    public ObservableCollection<FeedItem> Feed { get; } = new();

    public ICommand ShowOverviewCommand { get; private set; } = null!;
    public ICommand OpenOverviewRepoCommand { get; private set; } = null!;
    public ICommand ClearFeedCommand { get; private set; } = null!;
    public ICommand SetOverviewGroupCommand { get; private set; } = null!;

    private bool _overviewVisible;

    /// <summary>The overview replaces the home page while no repo is selected.</summary>
    public bool OverviewVisible
    {
        get => _overviewVisible;
        set
        {
            if (!Set(ref _overviewVisible, value)) return;
            OnPropertiesChanged(nameof(ShowHome), nameof(ShowOverview));
            if (value)
            {
                FeedUnread = 0;
                OverviewView.Refresh();
            }
        }
    }

    public bool ShowHome => !HasSelection && !OverviewVisible;
    public bool ShowOverview => !HasSelection && OverviewVisible;

    private int _feedUnread;

    /// <summary>Feed items added since the overview was last opened (badge on its button).</summary>
    public int FeedUnread { get => _feedUnread; private set => Set(ref _feedUnread, value); }

    /// <summary>"none", "workspace", "server" or "status".</summary>
    public string OverviewGroupBy
    {
        get => Settings.OverviewGroupBy;
        set
        {
            if (Settings.OverviewGroupBy == value) return;
            Settings.OverviewGroupBy = value;
            OnPropertyChanged();
            ApplyOverviewGrouping();
            SaveSettings();
        }
    }

    /// <summary>Called from the constructor: the overview's view and the saved feed (each repo's last known status is
    /// applied when its view model is made, see VmFor).</summary>
    private void InitOverview()
    {
        foreach (var item in ChangeFeed.Load()) Feed.Add(item);

        OverviewView = new ListCollectionView(Repos);
        OverviewView.Filter = o => o is RepoViewModel r && InScope(r);
        ApplyOverviewGrouping();

        ShowOverviewCommand = new RelayCommand(() =>
        {
            SelectedRepo = null;
            OverviewVisible = !OverviewVisible;
        });
        OpenOverviewRepoCommand = new RelayCommand(p =>
        {
            if (p is RepoViewModel repo) OpenRepo(repo, RepoDetailViewModel.TabOverview);
        });
        ClearFeedCommand = new RelayCommand(() =>
        {
            Feed.Clear();
            FeedUnread = 0;
            ChangeFeed.Save(Feed);
        });
        SetOverviewGroupCommand = new RelayCommand(p => OverviewGroupBy = p as string ?? "none");
    }

    private void ApplyOverviewGrouping()
    {
        using (OverviewView.DeferRefresh())
        {
            OverviewView.GroupDescriptions.Clear();
            var property = Settings.OverviewGroupBy switch
            {
                "workspace" => nameof(RepoViewModel.WorkspaceName),
                "server" => nameof(RepoViewModel.ServerHost),
                "status" => nameof(RepoViewModel.StatusGroup),
                _ => null,
            };
            if (property is not null) OverviewView.GroupDescriptions.Add(new PropertyGroupDescription(property));
            if (OverviewView.SortDescriptions.Count == 0)
            {
                OverviewView.SortDescriptions.Add(new SortDescription(nameof(RepoViewModel.Pinned), ListSortDirection.Descending));
                OverviewView.SortDescriptions.Add(new SortDescription(nameof(RepoViewModel.Name), ListSortDirection.Ascending));
            }
        }
    }

    /// <summary>Adds feed lines (newest first), keeps the newest <see cref="ChangeFeed.MaxItems"/>, and saves them.</summary>
    private void AddFeed(IReadOnlyList<FeedItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items) Feed.Insert(0, item);
        while (Feed.Count > ChangeFeed.MaxItems) Feed.RemoveAt(Feed.Count - 1);
        if (!ShowOverview) FeedUnread += items.Count;
        if (ChangeFeed.Save(Feed) is { } error) AddLog(error, LogLevel.Warning);
    }

    /// <summary>Every refreshed repo's status to status-cache.json, for an instant next start. Repos of profiles not
    /// loaded this session keep their earlier snapshot.</summary>
    private void SaveStatusCache()
    {
        foreach (var r in _repoVms.Values.Where(r => r.Loaded && !r.IsStale)) _snapshots[r.Path] = r.ToSnapshot();
        var paths = Settings.Repos.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (StatusCache.Save(_snapshots.Values.Where(s => paths.Contains(s.Path))) is { } error) AddLog(error, LogLevel.Warning);
    }
}
