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

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }

    public ObservableCollection<RepoViewModel> Repos { get; } = new();
    public ICollectionView ReposView { get; }
    public ObservableCollection<LogEntry> Log { get; } = new();
    public DashboardViewModel Dashboard { get; }

    private readonly IProgress<LogEntry> _log;
    private readonly DispatcherTimer _autoTimer = new();
    private DateTime _lastLocalRefresh = DateTime.MinValue;
    private readonly RepoWatcher _watcher;

    /// <summary>Raised for tray notifications (title, text).</summary>
    public event Action<string, string>? Notify;

    public MainViewModel()
    {
        Settings = SettingsService.Load();
        ForgeLinks.ExtraHosts = Settings.ExtraGitLabHosts;
        Workspaces.EnsureDefaults(Settings);
        Workspaces.Settings = Settings;
        _log = new Progress<LogEntry>(AddLog);
        if (SettingsService.LoadProblem is { } problem) AddLog(problem, LogLevel.Warning);
        AsyncCommand.ErrorHandler = ex =>
        {
            if (ex is UserFacingException ufe)
            {
                AddLog(ufe.Message, LogLevel.Error);
                foreach (var h in ufe.Help) AddLog("  " + h, LogLevel.Error);
            }
            else AddLog(ex.ToString(), LogLevel.Error);
            LogVisible = true;
        };

        // only the active profile's repos are loaded; the rest wait until their profile is picked
        _snapshots = StatusCache.Load();
        LoadScope();
        InitOverview();
        _watcher = new RepoWatcher(Dispatcher.CurrentDispatcher, OnRepoChangedOnDisk);
        foreach (var repo in Repos) _watcher.Watch(repo.Path);
        Repos.CollectionChanged += (_, e) =>
        {
            foreach (RepoViewModel r in e.OldItems ?? Array.Empty<RepoViewModel>()) _watcher.Unwatch(r.Path);
            foreach (RepoViewModel r in e.NewItems ?? Array.Empty<RepoViewModel>()) _watcher.Watch(r.Path);
        };
        ReposView = CollectionViewSource.GetDefaultView(Repos);
        ReposView.Filter = o => o is RepoViewModel r && MatchesFilter(r);
        ApplyGrouping();
        Dashboard = new DashboardViewModel(this);

        AddRepoCommand = new AsyncCommand(AddRepoAsync);
        ScanFolderCommand = new AsyncCommand(ScanFolderAsync);
        CloneCommand = new AsyncCommand(CloneAsync);
        AddSvnRepoCommand = new AsyncCommand(AddSvnRepoAsync);
        RefreshAllCommand = new AsyncCommand(() => RefreshReposAsync(ScopeRepos, fetch: false));
        FetchAllCommand = new AsyncCommand(() => RefreshReposAsync(ScopeRepos, fetch: true));
        PullAllCommand = new AsyncCommand(() => PullAllAsync());
        PushAllCommand = new AsyncCommand(() => PushAllAsync());
        CommitAllCommand = new AsyncCommand(() => CommitAllAsync());
        CommitSelectedCommand = new AsyncCommand(p => CommitAllAsync(Picked(p)));
        // the repo list's right-click menu passes its SelectedItems (Ctrl/Shift-click several cards)
        FetchSelectedCommand = new AsyncCommand(p => RefreshReposAsync(Picked(p), fetch: true));
        PullSelectedCommand = new AsyncCommand(p => PullAllAsync(Picked(p)));
        PushSelectedCommand = new AsyncCommand(p => PushAllAsync(Picked(p)));
        CleanupAllCommand = new AsyncCommand(CleanupAllAsync);
        TokenCommand = new RelayCommand(ManageAccounts);
        SettingsCommand = new RelayCommand(OpenSettings);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleLogCommand = new RelayCommand(() => LogVisible = !LogVisible);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        DismissBannerCommand = new RelayCommand(() => BannerVisible = false);
        SetFilterCommand = new RelayCommand(p => Filter = p as string ?? "all");
        SetWorkspaceCommand = new RelayCommand(p => ActiveWorkspace = p as string ?? Workspaces.AllId);
        SelectProfileNumberCommand = new RelayCommand(SelectProfileByNumber);
        ManageProfilesCommand = new RelayCommand(() => OpenSettings(profilesTab: true));
        HomeCommand = new RelayCommand(GoHome);

        RemoveRepoCommand = new RelayCommand(p => RemoveRepo(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        RenameRepoCommand = new RelayCommand(p => RenameRepo(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        RepoSettingsCommand = new AsyncCommand(p => EditRepoAsync(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        PinRepoCommand = new RelayCommand(p => TogglePin(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        WatchRepoCommand = new RelayCommand(p => ToggleWatch(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        OpenFolderCommand = new RelayCommand(p => Shell.OpenFolder((p as RepoViewModel ?? SelectedRepo)!.Path), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        OpenEditorCommand = new RelayCommand(p =>
        {
            var r = (p as RepoViewModel ?? SelectedRepo)!;
            Shell.OpenInEditor(Settings.EditorCommand, r.Path, r.Path);
        }, p => (p as RepoViewModel ?? SelectedRepo) is not null);
        EditGitIgnoreCommand = new AsyncCommand(async p =>
        {
            var r = p as RepoViewModel ?? SelectedRepo;
            if (r is null || !GitIgnoreWindow.Edit(r.Name, r.Path, Settings.EditorCommand)) return;
            AddLog($"{r.Name}: .gitignore saved", LogLevel.Info);
            await RefreshRepoAsync(r);
        }, p => (p as RepoViewModel ?? SelectedRepo) is not null);
        OpenTerminalCommand = new RelayCommand(p => Shell.OpenTerminal((p as RepoViewModel ?? SelectedRepo)!.Path), p => (p as RepoViewModel ?? SelectedRepo) is not null);
        RunQuickCommand = new AsyncCommand(RunQuickCommandAsync, CanRepo);
        OpenWebCommand = new RelayCommand(p =>
        {
            var r = (p as RepoViewModel ?? SelectedRepo)!;
            Shell.OpenUrl(ForgeLinks.Branch(r.Remote!, r.Branch));
        }, p => (p as RepoViewModel ?? SelectedRepo)?.Remote is not null);

        bool CanRepo() => SelectedRepo is { IsBusy: false, Loaded: true };
        FetchCommand = new AsyncCommand(() => RunRepoAsync(SelectedRepo!, "Fetch", async a => { await a.FetchAsync(); return "Fetched"; }), CanRepo);
        PullCommand = new AsyncCommand(() => RunRepoAsync(SelectedRepo!, "Pull", a => a.PullAsync()), CanRepo);
        PushCommand = new AsyncCommand(() => PushAsync(SelectedRepo!), CanRepo);
        SyncCommand = new AsyncCommand(() => RunRepoAsync(SelectedRepo!, "Pull & push", a => a.SyncAsync()), CanRepo);
        StartBranchCommand = new AsyncCommand(() => StartBranchAsync(SelectedRepo!), CanRepo);
        UpdateFromDefaultCommand = new AsyncCommand(() => RunRepoAsync(SelectedRepo!, "Update from default branch", a => a.UpdateFromDefaultAsync()),
            () => CanRepo() && SelectedRepo!.IsFeatureBranch);
        CreateMergeRequestCommand = new AsyncCommand(() => CreateMergeRequestAsync(SelectedRepo!), () => CanRepo() && SelectedRepo!.IsFeatureBranch && SelectedRepo.HasForge);
        FinishBranchCommand = new AsyncCommand(() => FinishBranchAsync(SelectedRepo!), () => CanRepo() && SelectedRepo!.IsFeatureBranch);
        FixIdentityCommand = new AsyncCommand(p => FixIdentityAsync(p as RepoViewModel ?? SelectedRepo!), p => (p as RepoViewModel ?? SelectedRepo) is { IdentityMismatch: true });
        StandupCommand = new RelayCommand(() => StandupWindow.Show(ScopeName,
            ScopeRepos.Select(r => (r.Name, r.Path, (IReadOnlyCollection<string>)new[] { r.GitEmail, r.Workspace.GitEmail })).ToList()));
        SearchReposCommand = new RelayCommand(() => SearchWindow.Open(ScopeName, ScopeRepos.Select(r => new SearchRepo(r.Name, r.Path, r.RemoteName, r.Remote)).ToList(),
            Settings.EditorCommand, RevealSearchHit));
        UndoLastActionCommand = new AsyncCommand(p => UndoLastActionAsync(p as RepoViewModel ?? SelectedRepo),
            p => (p as RepoViewModel ?? SelectedRepo) is { IsBusy: false, LastUndo: not null });
        CancelRepoCommand = new RelayCommand(p => (p as RepoViewModel ?? SelectedRepo)?.CancelWork(), p => (p as RepoViewModel ?? SelectedRepo) is { IsBusy: true });
        NextStepCommand = new AsyncCommand(p => RunNextStepAsync(p as RepoViewModel ?? SelectedRepo), p => (p as RepoViewModel ?? SelectedRepo) is { IsBusy: false });

        DismissGetStartedCommand = new RelayCommand(() =>
        {
            Settings.GetStartedDismissed = true;
            SaveSettings();
            RaiseGetStarted();
        });
        Repos.CollectionChanged += (_, _) => RaiseGetStarted();

        _autoTimer.Tick += async (_, _) => await AutoRefreshAsync();
        ApplyAutoTimer();
    }

    private RepoViewModel Track(RepoViewModel repo)
    {
        repo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RepoViewModel.State) or nameof(RepoViewModel.NeedsAttention) or nameof(RepoViewModel.Pinned)) RecountSoon();
            if (e.PropertyName is nameof(RepoViewModel.WorkspaceId)) RegroupSoon();
        };
        return repo;
    }

    // =================================================================================== commands

    public ICommand AddRepoCommand { get; }
    public ICommand ScanFolderCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand AddSvnRepoCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand FetchAllCommand { get; }
    public ICommand PullAllCommand { get; }
    public ICommand PushAllCommand { get; }
    public ICommand CommitAllCommand { get; }
    public ICommand CommitSelectedCommand { get; }
    public ICommand FetchSelectedCommand { get; }
    public ICommand PullSelectedCommand { get; }
    public ICommand PushSelectedCommand { get; }

    private static List<RepoViewModel> Picked(object? p) =>
        (p as System.Collections.IEnumerable)?.OfType<RepoViewModel>().ToList() ?? (p is RepoViewModel r ? new List<RepoViewModel> { r } : new List<RepoViewModel>());
    public ICommand CleanupAllCommand { get; }
    public ICommand TokenCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand SetWorkspaceCommand { get; }
    public ICommand SelectProfileNumberCommand { get; }
    public ICommand ManageProfilesCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand RemoveRepoCommand { get; }
    public ICommand RenameRepoCommand { get; }
    public ICommand RepoSettingsCommand { get; }
    public ICommand PinRepoCommand { get; }
    public ICommand WatchRepoCommand { get; }
    public ICommand EditGitIgnoreCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenEditorCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand RunQuickCommand { get; }
    public ICommand OpenWebCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand StartBranchCommand { get; }
    public ICommand CancelRepoCommand { get; }
    public ICommand UndoLastActionCommand { get; }
    public ICommand StandupCommand { get; }
    public ICommand SearchReposCommand { get; }
    public ICommand DismissGetStartedCommand { get; }

    // ---- first run: the Get started checklist on the home page ----

    public bool StepAccountsDone => TokenStore.Hosts.Count > 0;
    public bool StepIdentityDone => Settings.Workspaces.Any(w => w.GitEmail.Trim().Length > 0);
    public bool StepReposDone => Repos.Count > 0;
    public bool ShowGetStarted => !Settings.GetStartedDismissed && !(StepAccountsDone && StepIdentityDone && StepReposDone);

    private void RaiseGetStarted() =>
        OnPropertiesChanged(nameof(StepAccountsDone), nameof(StepIdentityDone), nameof(StepReposDone), nameof(ShowGetStarted));
    public ICommand UpdateFromDefaultCommand { get; }
    public ICommand CreateMergeRequestCommand { get; }
    public ICommand FinishBranchCommand { get; }
    public ICommand FixIdentityCommand { get; }
    public ICommand NextStepCommand { get; }

    // =================================================================================== selection

    private RepoViewModel? _selectedRepo;
    private RepoDetailViewModel? _detail;

    public RepoViewModel? SelectedRepo
    {
        get => _selectedRepo;
        set
        {
            if (!Set(ref _selectedRepo, value)) return;
            if (!_switchingProfile) RememberSelection(value?.Path);
            var tab = Detail?.SelectedTab ?? 0;
            Detail?.Detach();
            Detail = value is null ? null : new RepoDetailViewModel(this, value, tab);
            OnPropertiesChanged(nameof(HasSelection), nameof(WindowTitle), nameof(ShowHome), nameof(ShowOverview));
            _ = Detail?.LoadCurrentTabAsync();
            if (value is null) _ = Dashboard.LoadAsync();
        }
    }

    public bool HasSelection => SelectedRepo is not null;

    public RepoDetailViewModel? Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>Selects a repo and opens one of its tabs (from the home page).</summary>
    public void OpenRepo(RepoViewModel repo, int tab)
    {
        SelectedRepo = repo;
        Detail?.GoToTab(tab);
    }

    private void GoHome()
    {
        OverviewVisible = false;
        if (SelectedRepo is null) _ = Dashboard.LoadAsync();
        else SelectedRepo = null;
    }

    // =================================================================================== log / banner / status

    private string _statusText = "Ready";
    private int _busyCount;

    public bool LogVisible
    {
        get => Settings.LogVisible;
        set
        {
            if (Settings.LogVisible == value) return;
            Settings.LogVisible = value;
            OnPropertyChanged();
            if (value) UnseenErrors = 0;
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private int _unseenErrors;

    /// <summary>Errors written to the Activity log while it was hidden (the status bar offers to show them).</summary>
    public int UnseenErrors
    {
        get => _unseenErrors;
        private set
        {
            if (Set(ref _unseenErrors, value)) OnPropertyChanged(nameof(UnseenErrorsText));
        }
    }

    public string UnseenErrorsText => UnseenErrors == 1 ? "1 error - show" : $"{UnseenErrors} errors - show";
    public bool IsBusy => _busyCount > 0;

    public string AutoFetchText => ActiveWorkspace == Workspaces.AllId
        ? "Background fetch per profile"
        : Workspaces.Get(ActiveWorkspace).AutoFetchMinutes is > 0 and var m ? $"Background fetch every {m} min" : "Background fetch off";

    private void Busy(int delta)
    {
        _busyCount = Math.Max(0, _busyCount + delta);
        OnPropertyChanged(nameof(IsBusy));
    }

    public IProgress<LogEntry> LogSink => _log;

    public void AddLog(LogEntry entry)
    {
        Log.Add(entry);
        while (Log.Count > 3000) Log.RemoveAt(0);
        if (entry.Level is LogLevel.Success or LogLevel.Error or LogLevel.Warning) StatusText = entry.Message;
        if (entry.Level == LogLevel.Error && !LogVisible) UnseenErrors++;
    }

    public void AddLog(string message, LogLevel level = LogLevel.Info) => AddLog(new LogEntry(message, level));

    private bool _bannerVisible;
    private string _bannerTitle = string.Empty;
    private string _bannerText = string.Empty;
    private string _bannerTone = "info";

    public bool BannerVisible { get => _bannerVisible; set => Set(ref _bannerVisible, value); }
    public string BannerTitle { get => _bannerTitle; private set => Set(ref _bannerTitle, value); }
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }
    public string BannerTone { get => _bannerTone; private set => Set(ref _bannerTone, value); }

    private RepoViewModel? _bannerUndoRepo;

    /// <summary>The repo whose just-finished action the banner's Undo button takes back (null = no Undo button).</summary>
    public RepoViewModel? BannerUndoRepo { get => _bannerUndoRepo; private set => Set(ref _bannerUndoRepo, value); }

    public void ShowBanner(string title, string text, string tone, RepoViewModel? undo = null)
    {
        BannerTitle = title;
        BannerText = text;
        BannerTone = tone;
        BannerUndoRepo = undo;
        BannerVisible = true;
    }

    private async Task UndoLastActionAsync(RepoViewModel? repo)
    {
        if (repo?.LastUndo is not { } point) return;
        if (await RunRepoAsync(repo, $"Undo {point.Title.ToLowerInvariant()}", async a =>
            {
                await a.UndoToAsync(point);
                return "Back to where it was before";
            }, undoable: false))
        {
            if (UndoHistory.Find(repo.Path, point) is { } entry) entry.Undone = true;
            repo.LastUndo = null;
        }
    }

    // =================================================================================== settings

    private void ManageAccounts()
    {
        AccountsWindow.Open(SelectedRepo?.Remote);
        RaiseGetStarted();
        foreach (var r in Repos) r.RaiseTokenChanged();
        _ = RefreshReposAsync(Repos.Where(r => r.HasForge && r.Watched).ToList(), fetch: false);
    }

    private void OpenSettings() => OpenSettings(profilesTab: false);

    private void OpenSettings(bool profilesTab)
    {
        var saved = SettingsWindow.Edit(Settings, SelectedRepo?.Path, out var importFile, profilesTab);
        if (importFile is not null)
        {
            _ = ImportMigrationAsync(importFile);
            return;
        }
        if (saved) SettingsChanged();
    }

    private void SettingsChanged()
    {
        RaiseGetStarted();
        Workspaces.EnsureDefaults(Settings);
        ForgeLinks.ExtraHosts = Settings.ExtraGitLabHosts;
        ThemeService.Apply(ThemeService.FromSetting(Settings.Theme));
        Density.Apply(Settings.CompactDensity);
        ApplyAutoTimer();
        OnPropertyChanged(nameof(AutoFetchText));
        SaveSettings();
        foreach (var r in Repos) r.ResetRemoteCache();
        RaiseWorkspaces();
        _ = RefreshWatchedAsync(Repos.ToList());
    }

    /// <summary>Settings > Export / migrate > Import: clone or add the picked repos and take the picked settings.</summary>
    private async Task ImportMigrationAsync(string path)
    {
        MigrationFile file;
        try
        {
            file = MigrationImport.Read(path);
        }
        catch (Exception ex)
        {
            ShowBanner("Import failed", $"{Path.GetFileName(path)} is not a repo-sync export: {ex.Message}", "error");
            return;
        }

        var home = MigrationImport.Home;
        bool InList(string p) => Repos.Any(r => string.Equals(r.Path.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var repos = MigrationImport.PlanRepos(file, InList, home);
        var settings = MigrationImport.DiffSettings(Settings, file, home);
        settings.AddRange(await MigrationImport.DiffGitConfigAsync(file, home));

        if (ImportWindow.Ask(file, repos, settings) is not { } plan) return;

        AddLog($"Import from {file.Machine} ({Path.GetFileName(path)})", LogLevel.Header);
        Busy(+1);
        var added = new List<RepoViewModel>();
        var failed = new List<string>();
        try
        {
            if (plan.Settings.Count > 0)
            {
                await MigrationImport.ApplyGitConfigAsync(plan.Settings, _log, home);
                MigrationImport.ApplySettings(Settings, plan.Settings);
                SettingsChanged();
                AddLog($"Took {plan.Settings.Count} setting(s) from the file; kept the rest.", LogLevel.Success);
            }

            var i = 0;
            foreach (var item in plan.Repos)
            {
                i++;
                StatusText = $"Import {i} / {plan.Repos.Count}: {item.Name}...";
                try
                {
                    if (item.Action == ImportAction.Clone)
                    {
                        await RepoActions.CloneAsync(item.CloneUrl!, item.Target, _log, CancellationToken.None);
                        await MigrationImport.FinishCloneAsync(item, _log);
                    }
                    var profile = item.Source.WorkspaceOverride is { } ws && Workspaces.Exists(ws) ? ws
                        : Workspaces.Exists(item.Source.Workspace) ? item.Source.Workspace : null;
                    if (AddRepoPath(item.Target, profile) is not { } repo) continue;
                    var src = item.Source;
                    if (src.DisplayName is { Length: > 0 }) repo.RenameDisplay(src.DisplayName);
                    repo.Entry.Pinned = src.Pinned;
                    repo.Entry.Watched = src.Watched;
                    added.Add(repo);
                }
                catch (UserFacingException ex)
                {
                    failed.Add(item.Name);
                    AddLog(ex.Message, LogLevel.Error);
                    foreach (var h in ex.Help) AddLog("  " + h, LogLevel.Error);
                }
                catch (Exception ex)
                {
                    failed.Add(item.Name);
                    AddLog($"{item.Name}: {ex.Message}", LogLevel.Error);
                }
            }
            SaveSettings();
            ReposView.Refresh();
            await RefreshReposAsync(added.Where(Repos.Contains).ToList(), fetch: false);
        }
        finally
        {
            Busy(-1);
        }

        var summary = $"{added.Count} repo(s) added, {plan.Settings.Count} setting(s) taken from the file.";
        if (failed.Count > 0)
            ShowBanner("Import finished with problems", $"{summary} Could not clone: {string.Join(", ", failed)} (see Activity).", "warning");
        else
            ShowBanner("Import done", summary, "success");
        StatusText = summary;
    }

    private void ToggleTheme()
    {
        var next = ThemeService.Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
        ThemeService.Apply(next);
        Settings.Theme = next.ToString();
        SaveSettings();
    }

    public void SaveSettings()
    {
        if (SettingsService.Save(Settings) is { } error) AddLog(error, LogLevel.Error);
    }
}
