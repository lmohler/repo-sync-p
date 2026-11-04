using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class MainViewModel
{
    // =================================================================================== profiles (stored as workspaces)

    /// <summary>A profile id, or "all". Only the repos of this scope are loaded, watched and refreshed.</summary>
    public string ActiveWorkspace
    {
        get => Settings.ActiveWorkspace;
        set
        {
            if (Settings.ActiveWorkspace == value || (value != Workspaces.AllId && !Workspaces.Exists(value))) return;
            Settings.ActiveWorkspace = value;
            var added = LoadScope();
            OnPropertiesChanged(nameof(ActiveWorkspace), nameof(WindowTitle), nameof(ScopeName), nameof(ScopeBrush), nameof(AutoFetchText));
            ApplyGrouping();
            RecountSoon();
            // reopen what was open the last time this profile was picked (the old profile keeps its own memory)
            _switchingProfile = true;
            try
            {
                var last = RememberedSelection;
                SelectedRepo = Repos.FirstOrDefault(r => string.Equals(r.Path, last, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _switchingProfile = false;
            }
            if (SelectedRepo is null) _ = Dashboard.LoadAsync();
            ApplyAutoTimer();
            SaveSettings();
            _ = LoadReposAsync(added);
        }
    }

    private bool _switchingProfile;

    /// <summary>The repo last open in the loaded profile (or in "All profiles").</summary>
    public string? RememberedSelection =>
        ActiveWorkspace == Workspaces.AllId ? Settings.LastSelectedRepo : Workspaces.Get(ActiveWorkspace).LastSelectedRepo;

    private void RememberSelection(string? path)
    {
        if (ActiveWorkspace == Workspaces.AllId) Settings.LastSelectedRepo = path;
        else Workspaces.Get(ActiveWorkspace).LastSelectedRepo = path;
    }

    /// <summary>One row of the sidebar's profile picker ("All profiles" first).</summary>
    public sealed record ProfileChoice(string Id, string Name, SolidColorBrush Brush, int Count, string Shortcut)
    {
        public string CountText => Count == 1 ? "1 repo" : $"{Count} repos";
    }

    public ObservableCollection<ProfileChoice> ProfileChoices { get; } = new();

    /// <summary>Rebuilds the picker (names, colors, counts); keeps the selection.</summary>
    private void RebuildProfileChoices()
    {
        var rows = new List<ProfileChoice> { new(Workspaces.AllId, "All profiles", Workspaces.Brush("#8C959F"), Settings.Repos.Count, "Ctrl+0") };
        rows.AddRange(Workspaces.All.Select((w, i) => new ProfileChoice(w.Id, w.Name, Workspaces.Brush(w.Color),
            Settings.Repos.Count(e => e.Workspace == w.Id), i < 9 ? $"Ctrl+{i + 1}" : string.Empty)));
        if (rows.Select(r => (r.Id, r.Name, r.Brush.Color, r.Count)).SequenceEqual(ProfileChoices.Select(r => (r.Id, r.Name, r.Brush.Color, r.Count)))) return;
        ProfileChoices.Clear();
        foreach (var r in rows) ProfileChoices.Add(r);
        // the ComboBox loses its selection when its items are replaced
        OnPropertyChanged(nameof(ActiveWorkspace));
    }

    /// <summary>Ctrl+0 = all, Ctrl+1..9 = the profiles in their order.</summary>
    private void SelectProfileByNumber(object? p)
    {
        if (!int.TryParse(p as string, out var n)) return;
        if (n == 0) ActiveWorkspace = Workspaces.AllId;
        else if (n <= Workspaces.All.Count) ActiveWorkspace = Workspaces.All[n - 1].Id;
    }

    public string ScopeName => ActiveWorkspace == Workspaces.AllId ? "All profiles" : Workspaces.Get(ActiveWorkspace).Name;
    public SolidColorBrush ScopeBrush => ActiveWorkspace == Workspaces.AllId ? Workspaces.Brush("#8C959F") : Workspaces.Brush(Workspaces.Get(ActiveWorkspace).Color);

    /// <summary>Where Add repo / Scan put new repos: the loaded profile, or the first one while all are loaded.</summary>
    public string TargetWorkspace => ActiveWorkspace == Workspaces.AllId ? Workspaces.All[0].Id : ActiveWorkspace;

    /// <summary>From &lt;Version&gt; in Directory.Build.props (the publish workflow sets it from the git tag).</summary>
    public static string AppVersion { get; } =
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public string WindowTitle =>
        $"Repo Sync {AppVersion} - {(SelectedRepo is { } r ? $"{r.Name} ({r.WorkspaceName})" : ScopeName)}";

    public bool InScope(RepoViewModel r) => ActiveWorkspace == Workspaces.AllId || r.WorkspaceId == ActiveWorkspace;

    // ---- loading: a view model is made once per repo and kept, so switching back to a profile is instant

    private readonly Dictionary<RepoEntry, RepoViewModel> _repoVms = new();
    private Dictionary<string, RepoSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private RepoViewModel VmFor(RepoEntry entry)
    {
        if (_repoVms.TryGetValue(entry, out var repo)) return repo;
        repo = Track(new RepoViewModel(entry));
        // last known status, shown at once and marked stale until the first refresh replaces it
        if (_snapshots.TryGetValue(entry.Path, out var snapshot)) repo.ApplySnapshot(snapshot);
        _repoVms[entry] = repo;
        return repo;
    }

    private void ForgetVm(RepoViewModel repo) => _repoVms.Remove(repo.Entry);

    /// <summary>Makes <see cref="Repos"/> hold exactly the active scope's repos. Returns the ones that came in.</summary>
    private List<RepoViewModel> LoadScope()
    {
        var wanted = Settings.Repos
            .Where(e => ActiveWorkspace == Workspaces.AllId || e.Workspace == ActiveWorkspace)
            .Select(VmFor).ToList();
        var keep = wanted.ToHashSet();
        foreach (var gone in Repos.Where(r => !keep.Contains(r)).ToList()) Repos.Remove(gone);
        var added = wanted.Where(r => !Repos.Contains(r)).ToList();
        foreach (var r in added) Repos.Add(r);
        RebuildProfileChoices();
        return added;
    }

    /// <summary>Repos that just came in: their worktrees, then a refresh (server only for watched repos), and a quiet
    /// fetch where the profile has background fetch on.</summary>
    private async Task LoadReposAsync(List<RepoViewModel> repos)
    {
        if (repos.Count == 0) return;
        // cards show their cached (stale) status until this finishes; say so in the status bar
        var label = $"Loading {ScopeName}: {repos.Count} repo{(repos.Count == 1 ? "" : "s")}...";
        Busy(+1);
        StatusText = label;
        try
        {
            repos.AddRange(await AddLinkedWorktreesAsync(repos));
            await RefreshWatchedAsync(repos);
            var fetch = repos.Where(r => r.Watched && r.Workspace.AutoFetchMinutes > 0).ToList();
            foreach (var id in fetch.Select(r => r.WorkspaceId).Distinct()) _lastAutoFetch[id] = DateTime.Now;
            if (fetch.Count > 0)
            {
                StatusText = $"{label} fetching {fetch.Count}";
                await RefreshReposAsync(fetch, fetch: true, quiet: true);
            }
        }
        finally
        {
            Busy(-1);
            if (StatusText.StartsWith(label, StringComparison.Ordinal)) StatusText = "Ready";
        }
    }

    /// <summary>After a repo moved to another profile: follow it there, so it stays in view and selected.</summary>
    private void FollowRepo(RepoViewModel repo)
    {
        RebuildProfileChoices();
        if (!InScope(repo)) ActiveWorkspace = repo.WorkspaceId;
        ReposView.Refresh();
        RecountSoon();
        OnPropertyChanged(nameof(WindowTitle));
        if (SelectedRepo is null && Repos.Contains(repo)) SelectedRepo = repo;
    }

    /// <summary>Card menu > Move to profile: the repos leave the list unless that profile is loaded too.</summary>
    public void MoveReposToProfile(IReadOnlyCollection<RepoViewModel> repos, string id)
    {
        if (!Workspaces.Exists(id)) return;
        var moving = repos.Where(r => r.WorkspaceId != id).ToList();
        if (moving.Count == 0) return;
        foreach (var r in moving) r.SetWorkspace(id);
        SaveSettings();
        if (SelectedRepo is { } s && moving.Contains(s) && !InScope(s)) SelectedRepo = null;
        LoadScope();
        RecountSoon();
        var name = Workspaces.Get(id).Name;
        var what = moving.Count == 1 ? moving[0].Name : $"{moving.Count} repos";
        AddLog($"Moved {what} to {name}.", LogLevel.Success);
        ShowBanner($"Moved {what} to {name}", ActiveWorkspace == Workspaces.AllId || ActiveWorkspace == id
            ? "They stay in the list under their new profile." : $"Pick {name} in the profile list to see them.", "success");
    }

    /// <summary>The repos the profile picker shows; bulk actions and the home page work on these.</summary>
    public List<RepoViewModel> ScopeRepos => Repos.Where(InScope).ToList();

    /// <summary>All: grouped by profile. One profile: a flat list. Pinned first, then by name.</summary>
    private void ApplyGrouping()
    {
        using (ReposView.DeferRefresh())
        {
            ReposView.GroupDescriptions.Clear();
            ReposView.SortDescriptions.Clear();
            if (ActiveWorkspace == Workspaces.AllId)
            {
                ReposView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RepoViewModel.WorkspaceName)));
                ReposView.SortDescriptions.Add(new SortDescription(nameof(RepoViewModel.WorkspaceOrder), ListSortDirection.Ascending));
            }
            ReposView.SortDescriptions.Add(new SortDescription(nameof(RepoViewModel.Pinned), ListSortDirection.Descending));
            ReposView.SortDescriptions.Add(new SortDescription(nameof(RepoViewModel.Name), ListSortDirection.Ascending));
        }
        OverviewView.Refresh();
    }

    private bool _regroupQueued;

    private void RegroupSoon()
    {
        if (_regroupQueued) return;
        _regroupQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _regroupQueued = false;
            ReposView.Refresh();
            RecountSoon();
            OnPropertyChanged(nameof(WindowTitle));
        });
    }

    /// <summary>After profiles were added, removed, renamed or recolored (Settings > Profiles).</summary>
    private void RaiseWorkspaces()
    {
        foreach (var r in _repoVms.Values) r.RaiseWorkspace();
        var added = LoadScope();
        OnPropertiesChanged(nameof(ActiveWorkspace), nameof(ScopeName), nameof(ScopeBrush), nameof(WindowTitle), nameof(AutoFetchText));
        ApplyGrouping();
        RecountSoon();
        if (SelectedRepo is not null && !InScope(SelectedRepo)) SelectedRepo = null;
        ApplyAutoTimer();
        _ = LoadReposAsync(added);
    }

    // =================================================================================== filter + counts

    private string _filter = "all";
    private string _search = string.Empty;

    private string _quickCommandText = string.Empty;
    public string QuickCommandText { get => _quickCommandText; set => Set(ref _quickCommandText, value); }

    /// <summary>Runs the quick-bar text in the selected repo; asks first when it could throw work away.</summary>
    public async Task RunQuickCommandAsync()
    {
        var text = QuickCommandText.Trim();
        if (text.Length == 0 || SelectedRepo is not { } repo) return;

        var args = RepoActions.ParseArguments(text);
        if (args.Count > 0 && args[0].Equals("git", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        if (RepoActions.DestructiveReason(args) is { } reason &&
            !PromptWindow.Confirm("Run git command", $"git {string.Join(' ', args)}\n\nin {repo.Name}\n\n{reason}", "Run it", danger: true))
            return;

        var history = Settings.QuickCommandHistory;
        history.Remove(text);
        history.Add(text);
        if (history.Count > 50) history.RemoveRange(0, history.Count - 50);
        SaveSettings();

        QuickCommandText = string.Empty;
        await RunRepoAsync(repo, $"git {text}", async a =>
        {
            var r = await a.RunCustomGitAsync(text);
            return r.Success ? "Done" : $"git exited with code {r.ExitCode}";
        });
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ReposView.Refresh();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ReposView.Refresh();
        }
    }

    private bool MatchesFilter(RepoViewModel r)
    {
        if (!InScope(r)) return false;
        if (_search.Length > 0
            && !r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !r.Branch.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !r.Path.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !(r.MergeRequest?.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
            && !(r.Remote?.ProjectPath.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return _filter switch
        {
            "pinned" => r.Pinned,
            "attention" => r.NeedsAttention,
            "changes" => r.IsDirty,
            "unpushed" => r.Ahead > 0 || r.State == RepoState.Unpublished,
            "failing" => r.PipelineKind == "failed",
            "branches" => r.IsFeatureBranch,
            "identity" => r.IdentityMismatch,
            _ => true,
        };
    }

    private IEnumerable<RepoViewModel> Scoped => Repos.Where(InScope);

    public int TotalCount => Scoped.Count();
    public int AttentionCount => Scoped.Count(r => r.NeedsAttention);
    public int ChangesCount => Scoped.Count(r => r.IsDirty);
    public int UnpushedCount => Scoped.Count(r => r.Ahead > 0 || r.State == RepoState.Unpublished);
    public int FailingCount => Scoped.Count(r => r.PipelineKind == "failed");
    public int BranchCount => Scoped.Count(r => r.IsFeatureBranch);
    public int IdentityCount => Scoped.Count(r => r.IdentityMismatch);
    public int PinnedCount => Scoped.Count(r => r.Pinned);
    /// <summary>Nothing in the top bar's alert counts (identity is counted separately from NeedsAttention).</summary>
    public bool AllClear => TotalCount > 0 && AttentionCount == 0 && IdentityCount == 0;


    private bool _recountQueued;

    private void RecountSoon()
    {
        if (_recountQueued) return;
        _recountQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _recountQueued = false;
            OnPropertiesChanged(nameof(TotalCount), nameof(AttentionCount), nameof(ChangesCount), nameof(UnpushedCount),
                nameof(FailingCount), nameof(BranchCount), nameof(IdentityCount), nameof(PinnedCount), nameof(AllClear));
            if (_filter != "all") ReposView.Refresh();
        });
    }
}
