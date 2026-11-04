using RepoSyncV3.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private GridLength _logHeight = new(170);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        var s = _vm.Settings;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        // tray icon: notification balloons, a status dot, and (with Minimize to tray) the way back to the window
        _baseIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Text = "Repo Sync v3",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Open Repo Sync", null, (_, _) => BringToFront());
        _tray.ContextMenuStrip.Items.Add("Fetch all", null, (_, _) => _vm.FetchAllCommand.Execute(null));
        _tray.ContextMenuStrip.Items.Add("Pull all (fast-forward)", null, (_, _) => _vm.PullAllCommand.Execute(null));
        _tray.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        _tray.BalloonTipClicked += (_, _) => BringToFront();
        _tray.DoubleClick += (_, _) => BringToFront();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _vm.Settings.MinimizeToTray) Hide();
        };
        _vm.Notify += (title, text) => _tray.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.Info);

        _vm.PropertyChanged += Vm_PropertyChanged;
        ((INotifyCollectionChanged)_vm.Log).CollectionChanged += Log_CollectionChanged;
        ApplyLogVisibility();

        BindShortcuts();
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Activated += async (_, _) => await _vm.OnActivatedAsync();
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ keyboard shortcuts

    /// <summary>What each id in the Shortcuts table runs. Ids missing here (F5, Ctrl+Z, Alt+Home...) are bound in MainWindow.xaml.</summary>
    private Dictionary<string, Action> ShortcutActions()
    {
        void Run(ICommand? cmd, object? param = null)
        {
            if (cmd is not null && cmd.CanExecute(param)) cmd.Execute(param);
        }
        var d = new Dictionary<string, Action>
        {
            ["palette"] = ShowPalette,
            ["help"] = ShortcutsWindow.Open,
            ["quick-command"] = () =>
            {
                if (!_vm.LogVisible) _vm.LogVisible = true;
                QuickCommandBox.Focus();
                QuickCommandBox.SelectAll();
            },
            ["filter-repos"] = () =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            },
            ["pull-all"] = () => Run(_vm.PullAllCommand),
            ["push-all"] = () => Run(_vm.PushAllCommand),
            ["commit-all"] = () => Run(_vm.CommitAllCommand),
            ["cleanup-all"] = () => Run(_vm.CleanupAllCommand),
            ["standup"] = () => Run(_vm.StandupCommand),
            ["clone"] = () => Run(_vm.CloneCommand),
            ["add-repo"] = () => Run(_vm.AddRepoCommand),
            ["scan"] = () => Run(_vm.ScanFolderCommand),
            ["add-svn"] = () => Run(_vm.AddSvnRepoCommand),
            ["accounts"] = () => Run(_vm.TokenCommand),
            ["theme"] = () => Run(_vm.ToggleThemeCommand),
            ["log"] = () => Run(_vm.ToggleLogCommand),
            ["settings"] = () => Run(_vm.SettingsCommand),
            ["overview"] = () => Run(_vm.ShowOverviewCommand),
            ["fetch"] = () => Run(_vm.FetchCommand),
            ["pull"] = () => Run(_vm.PullCommand),
            ["push"] = () => Run(_vm.PushCommand),
            ["sync"] = () => Run(_vm.SyncCommand),
            ["start-branch"] = () => Run(_vm.StartBranchCommand),
            ["switch-branch"] = () => _vm.Detail?.GoToTab(RepoDetailViewModel.TabBranches),
            ["update"] = () => Run(_vm.UpdateFromDefaultCommand),
            ["create-mr"] = () => Run(_vm.CreateMergeRequestCommand),
            ["finish"] = () => Run(_vm.FinishBranchCommand),
            ["stage-all"] = () => Run(_vm.Detail?.StageAllCommand),
            ["stash"] = () => Run(_vm.Detail?.StashCommand),
            ["open-folder"] = () => Run(_vm.OpenFolderCommand),
            ["open-editor"] = () => Run(_vm.OpenEditorCommand),
            ["open-terminal"] = () => Run(_vm.OpenTerminalCommand),
            ["open-web"] = () => Run(_vm.OpenWebCommand),
            ["refresh-tab"] = () => _ = _vm.Detail?.LoadCurrentTabAsync(),
        };
        return d;
    }

    private Dictionary<string, Action> _shortcutActions = new();

    private void BindShortcuts()
    {
        _shortcutActions = ShortcutActions();
        foreach (var s in Shortcuts.All)
        {
            if (_shortcutActions.TryGetValue(s.Id, out var run))
                InputBindings.Add(new KeyBinding(new RelayCommand(run), s.Key, s.Modifiers));
        }
        // Ctrl+P still opens the palette (it did before Ctrl+K)
        InputBindings.Add(new KeyBinding(new RelayCommand(ShowPalette), Key.P, ModifierKeys.Control));
    }

    // ------------------------------------------------------------------ command palette

    /// <summary>Ctrl+K: jump to any repo, branch or tab, or run an action, by typing. Shortcuts show next to the actions.</summary>
    private bool _paletteOpen;

    private async void ShowPalette()
    {
        if (_paletteOpen) return;
        _paletteOpen = true;
        try { await ShowPaletteAsync(); }
        finally { _paletteOpen = false; }
    }

    private async Task ShowPaletteAsync()
    {
        var items = new List<PaletteItem>();
        void Add(string title, string group, ICommand? cmd, object? param = null, string? shortcutId = null)
        {
            if (cmd is not null && cmd.CanExecute(param))
                items.Add(new PaletteItem(title, group, () => cmd.Execute(param), shortcutId is null ? string.Empty : Shortcuts.TextOf(shortcutId)));
        }

        if (_vm.Detail is { } d)
        {
            var name = d.Repo.Name;
            var tabs = new (string Title, int Tab)[]
            {
                ("Overview", RepoDetailViewModel.TabOverview), ("Changes", RepoDetailViewModel.TabChanges), ("Branches", RepoDetailViewModel.TabBranches),
                ("History", RepoDetailViewModel.TabHistory), ("Merge requests", RepoDetailViewModel.TabMergeRequests), ("Pipelines", RepoDetailViewModel.TabPipelines),
                ("Stashes", RepoDetailViewModel.TabStashes), ("Tags", RepoDetailViewModel.TabTags), ("Worktrees", RepoDetailViewModel.TabWorktrees),
            };
            for (var i = 0; i < tabs.Length; i++)
            {
                var (title, tab) = tabs[i];
                if (d.GoToTabCommand.CanExecute(tab.ToString()))
                    items.Add(new PaletteItem($"Go to {title}", name, () => d.GoToTabCommand.Execute(tab.ToString()), $"Alt+{i + 1}"));
            }
            if (d.Repo.IsSvn) Add("Go to SVN", name, d.GoToTabCommand, RepoDetailViewModel.TabSvn.ToString());
            Add("Fetch", name, _vm.FetchCommand, null, "fetch");
            Add("Pull", name, _vm.PullCommand, null, "pull");
            Add("Push", name, _vm.PushCommand, null, "push");
            Add("Pull & push", name, _vm.SyncCommand, null, "sync");
            Add("Commit", name, d.CommitCommand);
            Add("Commit & push", name, d.CommitPushCommand);
            Add("Stage all", name, d.StageAllCommand, null, "stage-all");
            Add("Stash changes", name, d.StashCommand, null, "stash");
            Add("Undo last commit", name, d.UndoLastCommitCommand);
            if (d.Repo.LastUndo is not null) Add(d.Repo.UndoText, name, _vm.UndoLastActionCommand, d.Repo, "undo");
            if (d.Repo.IsBusy) Add("Cancel the running action", name, _vm.CancelRepoCommand, d.Repo);
            Add("Reload this tab", name, new RelayCommand(() => _ = d.LoadCurrentTabAsync()), null, "refresh-tab");
            Add("Undo history...", name, _vm.UndoHistoryCommand, d.Repo);
            Add("Recover (reflog)...", name, d.RecoverCommand);
            Add("Repo health...", name, d.HealthCommand);
            Add("Interactive rebase...", name, d.InteractiveRebaseCommand);
            Add("Remotes & submodules...", name, d.RemotesCommand);
            Add("Start a branch...", name, _vm.StartBranchCommand, null, "start-branch");
            Add("Update from default branch", name, _vm.UpdateFromDefaultCommand, null, "update");
            Add("Create merge request...", name, _vm.CreateMergeRequestCommand, null, "create-mr");
            Add("Finish branch", name, _vm.FinishBranchCommand, null, "finish");
            Add("Open folder", name, _vm.OpenFolderCommand, d.Repo, "open-folder");
            Add("Open in terminal", name, _vm.OpenTerminalCommand, d.Repo, "open-terminal");
            Add("Open in editor", name, _vm.OpenEditorCommand, d.Repo, "open-editor");
            Add("Open on the web", name, _vm.OpenWebCommand, d.Repo, "open-web");
            Add("Edit .gitignore...", name, _vm.EditGitIgnoreCommand, d.Repo);
            Add("Repo settings...", name, _vm.RepoSettingsCommand, d.Repo);

            // every branch of this repo: local ones switch, server-only ones check out a tracking branch
            if (d.Repo.Loaded && !d.Repo.IsBusy && System.IO.Directory.Exists(d.Repo.Path))
            {
                try
                {
                    var local = await Git.GetLocalBranchesAsync(d.Repo.Path);
                    foreach (var b in local.Where(b => !b.IsCurrent))
                        Add($"Switch to {b.Name}", $"{name} branch", d.SwitchBranchCommand, b);
                    if (d.Repo.RemoteName is { } remote)
                    {
                        var names = local.Select(b => b.Name).ToHashSet();
                        foreach (var b in await Git.GetRemoteBranchesAsync(d.Repo.Path, remote))
                            if (!names.Contains(b.Name[(remote.Length + 1)..]))
                                Add($"Check out {b.Name}", $"{name} server branch", d.CheckoutRemoteCommand, b);
                    }
                }
                catch (Exception ex)
                {
                    _vm.AddLog($"Command palette: could not list branches ({ex.Message})", LogLevel.Warning);
                }
            }
        }
        foreach (var r in _vm.Repos.OrderBy(r => r.Name))
            items.Add(new PaletteItem($"Open {r.Name}", $"repo - {r.WorkspaceName}", () => _vm.SelectedRepo = r));

        const string app = "App";
        Add("Home", app, _vm.HomeCommand, null, "home");
        Add("Overview (all repos)", app, _vm.ShowOverviewCommand, null, "overview");
        if (_vm.Detail is null) Add("Undo history...", app, _vm.UndoHistoryCommand);
        Add("Refresh all", app, _vm.RefreshAllCommand, null, "refresh-all");
        Add("Fetch all", app, _vm.FetchAllCommand, null, "fetch-all");
        Add("Pull all", app, _vm.PullAllCommand, null, "pull-all");
        Add("Push all", app, _vm.PushAllCommand, null, "push-all");
        Add("Commit all...", app, _vm.CommitAllCommand, null, "commit-all");
        Add("Clean up merged branches (all repos)...", app, _vm.CleanupAllCommand, null, "cleanup-all");
        Add("What I did (standup summary)...", app, _vm.StandupCommand, null, "standup");
        Add("Search all repos (commits, branches, MRs, code)...", app, _vm.SearchReposCommand, null, "search");
        Add("Search the repo list", app, new RelayCommand(_shortcutActions["filter-repos"]), null, "filter-repos");
        Add("Quick git command", app, new RelayCommand(_shortcutActions["quick-command"]), null, "quick-command");
        Add("Clone...", app, _vm.CloneCommand, null, "clone");
        Add("Add repo...", app, _vm.AddRepoCommand, null, "add-repo");
        Add("Scan a folder...", app, _vm.ScanFolderCommand, null, "scan");
        Add("Add SVN repo...", app, _vm.AddSvnRepoCommand, null, "add-svn");
        Add("Accounts...", app, _vm.TokenCommand, null, "accounts");
        Add("Toggle theme", app, _vm.ToggleThemeCommand, null, "theme");
        Add("Show / hide activity log", app, _vm.ToggleLogCommand, null, "log");
        Add("Settings...", app, _vm.SettingsCommand, null, "settings");
        Add("Keyboard shortcuts", app, new RelayCommand(ShortcutsWindow.Open), null, "help");
        foreach (var p in _vm.ProfileChoices)
            items.Add(new PaletteItem($"Profile: {p.Name}", app, () => _vm.SetWorkspaceCommand.Execute(p.Id), p.Shortcut));
        Add("Manage profiles...", app, _vm.ManageProfilesCommand);

        var w = new CommandPaletteWindow(items);
        w.ShowDialog();
        w.Picked?.Run();
    }
    private void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private readonly System.Drawing.Icon _baseIcon;
    private string _trayTone = string.Empty;

    /// <summary>Tray dot + tooltip from the header counts: red = failing / needs you, amber = work to push or commit, green = all clear.</summary>
    private void UpdateTray()
    {
        var tone = _vm.FailingCount + _vm.AttentionCount > 0 ? "error" : _vm.UnpushedCount + _vm.ChangesCount > 0 ? "warning" : "success";
        var parts = new List<string>();
        if (_vm.AttentionCount > 0) parts.Add($"{_vm.AttentionCount} need you");
        if (_vm.FailingCount > 0) parts.Add($"{_vm.FailingCount} failing");
        if (_vm.ChangesCount > 0) parts.Add($"{_vm.ChangesCount} with changes");
        if (_vm.UnpushedCount > 0) parts.Add($"{_vm.UnpushedCount} to push");
        var text = "Repo Sync: " + (parts.Count == 0 ? "all clear" : string.Join(", ", parts));
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon's limit
        if (tone == _trayTone) return;
        _trayTone = tone;
        _tray.Icon = TrayIcon.With(_baseIcon, tone);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
        }
        _vm.SaveSettings();
        _vm.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogVisible)) ApplyLogVisibility();
        if (e.PropertyName is nameof(MainViewModel.AttentionCount) or nameof(MainViewModel.FailingCount)
            or nameof(MainViewModel.ChangesCount) or nameof(MainViewModel.UnpushedCount)) UpdateTray();
    }

    private void ApplyLogVisibility()
    {
        if (_vm.LogVisible)
        {
            LogRow.Height = _logHeight;
            LogPanel.Visibility = Visibility.Visible;
            LogSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (LogRow.Height.Value > 0) _logHeight = LogRow.Height;
            LogRow.Height = new GridLength(0);
            LogPanel.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void Log_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // deferred: scrolling inside the CollectionChanged notification forces a layout pass before the
        // ListBox itself has seen the new item, which WPF reports as "inconsistent with its items source"
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_vm.Log.Count > 0) LogList.ScrollIntoView(_vm.Log[^1]);
        });
    }

    // ------------------------------------------------------------------ changes tab

    // ------------------------------------------------------------------ quick git command bar

    /// <summary>Up / Down walk the saved history (Settings.QuickCommandHistory); Enter runs the command.</summary>
    private int _historyIndex = -1;

    private void QuickCommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var history = _vm.Settings.QuickCommandHistory;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _historyIndex = -1;
            if (_vm.RunQuickCommand.CanExecute(null)) _vm.RunQuickCommand.Execute(null);
        }
        else if (e.Key is Key.Up or Key.Down && history.Count > 0)
        {
            e.Handled = true;
            if (_historyIndex < 0) _historyIndex = history.Count;
            _historyIndex += e.Key == Key.Up ? -1 : 1;
            if (_historyIndex < 0) _historyIndex = 0;
            if (_historyIndex >= history.Count)
            {
                _historyIndex = -1;
                QuickCommandBox.Text = string.Empty;
                return;
            }
            QuickCommandBox.Text = history[_historyIndex];
            QuickCommandBox.CaretIndex = QuickCommandBox.Text.Length;
        }
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedRepo is { } r) Shell.OpenTerminal(r.Path);
    }
    private void RepoListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            // right-click inside a multi-selection keeps it (for "Fetch / Pull / Push selected")
            if (!item.IsSelected) RepoList.SelectedItems.Clear();
            item.IsSelected = true;
            item.Focus();
        }
    }

    // any action in the header's "More" popup closes it
    private void RepoMoreMenu_Click(object sender, RoutedEventArgs e) => RepoMoreToggle.IsChecked = false;

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShortcutsWindow.Open();

    /// <summary>The Add repo button opens its menu below itself on a left click.</summary>
    private void AddRepoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (AddRepoMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AddRepoMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = _vm;
        menu.IsOpen = true;
    }

    /// <summary>Fills "Move to profile" with every profile; the picked cards (several with Ctrl/Shift-click) move together.</summary>
    private void RepoMenu_Opened(object sender, RoutedEventArgs e)
    {
        var picked = RepoList.SelectedItems.OfType<RepoViewModel>().ToList();
        MoveToProfileMenu.Items.Clear();
        MoveToProfileMenu.Header = picked.Count > 1 ? $"Move {picked.Count} repos to profile" : "Move to profile";
        MoveToProfileMenu.IsEnabled = picked.Count > 0 && Workspaces.All.Count > 1;
        foreach (var w in Workspaces.All)
        {
            var id = w.Id;
            var item = new MenuItem
            {
                Header = w.Name,
                Icon = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Workspaces.Brush(w.Color) },
                IsEnabled = picked.Any(r => r.WorkspaceId != id),
            };
            item.Click += (_, _) => _vm.MoveReposToProfile(picked, id);
            MoveToProfileMenu.Items.Add(item);
        }
    }
}
