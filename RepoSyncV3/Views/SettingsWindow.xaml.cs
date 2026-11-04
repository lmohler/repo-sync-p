using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

public partial class SettingsWindow : Window
{
    /// <summary>One profile in the Profiles tab: an edited copy, saved into the settings only on Save.</summary>
    public sealed class ProfileRow
    {
        public required WorkspaceSettings Profile { get; init; }
        public int Count { get; set; }
        public string Name => Profile.Name;
        public System.Windows.Media.SolidColorBrush Brush => Workspaces.Brush(Profile.Color);
        public string CountText => Count == 1 ? "1 repo" : $"{Count} repos";
    }

    private readonly AppSettings _settings;
    private string _origName = string.Empty;
    private string _origEmail = string.Empty;

    private readonly List<ProfileRow> _profiles;
    private ProfileRow? _editing;
    /// <summary>Removed profile id -> the profile its repos move to.</summary>
    private readonly Dictionary<string, string> _removed = new();

    private SettingsWindow(AppSettings settings, string? repoPath, bool profilesTab)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _settings = settings;
        ThemeBox.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        PatternBox.Text = settings.BranchPattern;
        EditorBox.Text = settings.EditorCommand;
        TicketUrlBox.Text = settings.TicketUrl;
        HostsBox.Text = string.Join(", ", settings.ExtraGitLabHosts);
        NotifyBox.IsChecked = settings.Notifications;
        TrayBox.IsChecked = settings.MinimizeToTray;
        CompactBox.IsChecked = settings.CompactDensity;

        _profiles = settings.Workspaces.Select(w => new ProfileRow
        {
            Profile = Copy(w),
            Count = settings.Repos.Count(e => e.Workspace == w.Id),
        }).ToList();
        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedItem = _profiles.FirstOrDefault(r => r.Profile.Id == settings.ActiveWorkspace) ?? _profiles[0];
        Select(IdentityCheckBox, settings.IdentityCheck);
        if (profilesTab) Tabs.SelectedItem = ProfilesTab;

        Loaded += async (_, _) =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            (_origName, _origEmail) = await RepoActions.GetIdentityAsync(home);
            NameBox.Text = _origName;
            EmailBox.Text = _origEmail;
        };
    }

    private static WorkspaceSettings Copy(WorkspaceSettings w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Color = w.Color,
        GitName = w.GitName,
        GitEmail = w.GitEmail,
        CloneRoot = w.CloneRoot,
        Notifications = w.Notifications,
        AutoFetchMinutes = w.AutoFetchMinutes,
        LastSelectedRepo = w.LastSelectedRepo,
    };

    // ---------------------------------------------------------------------------------- profiles tab

    /// <summary>Shows a profile in the editor (after keeping what was typed for the previous one).</summary>
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Keep();
        _editing = ProfileList.SelectedItem as ProfileRow;
        ProfileEditor.IsEnabled = _editing is not null;
        RemoveProfileButton.IsEnabled = _profiles.Count > 1;
        if (_editing?.Profile is not { } w) return;
        ProfileName.Text = w.Name;
        ProfileColor.Text = w.Color;
        ProfileGitName.Text = w.GitName;
        ProfileGitEmail.Text = w.GitEmail;
        ProfileClone.Text = w.CloneRoot;
        ProfileFetch.Text = (w.AutoFetchMinutes ?? 0).ToString();
        ProfileNotify.IsChecked = w.Notifications;
        ProfileHeading.Text = w.Name;
    }

    /// <summary>The editor's fields into the profile being edited.</summary>
    private void Keep()
    {
        if (_editing?.Profile is not { } w) return;
        w.Name = string.IsNullOrWhiteSpace(ProfileName.Text) ? w.Name : ProfileName.Text.Trim();
        w.Color = ProfileColor.Text.Trim().StartsWith('#') ? ProfileColor.Text.Trim() : w.Color;
        w.GitName = ProfileGitName.Text.Trim();
        w.GitEmail = ProfileGitEmail.Text.Trim();
        w.CloneRoot = ProfileClone.Text.Trim();
        w.AutoFetchMinutes = int.TryParse(ProfileFetch.Text.Trim(), out var m) && m >= 0 ? m : w.AutoFetchMinutes;
        w.Notifications = ProfileNotify.IsChecked == true;
    }

    private void ProfileName_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProfileHeading is not null) ProfileHeading.Text = ProfileName.Text.Trim();
    }

    private void RefreshProfileList(ProfileRow? select)
    {
        ProfileList.Items.Refresh();
        ProfileList.SelectedItem = select;
        RemoveProfileButton.IsEnabled = _profiles.Count > 1;
    }

    private static readonly string[] NewProfileColors = { "#8E44AD", "#D9822B", "#C0392B", "#2D9CDB", "#27AE60", "#B7950B" };

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask("New profile", "Name of the new profile (e.g. School, Open source):", string.Empty, "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        Keep();
        var taken = _profiles.Select(r => r.Profile.Id).Concat(_settings.Workspaces.Select(w => w.Id)).ToHashSet();
        string id;
        do id = "p-" + Guid.NewGuid().ToString("N")[..8];
        while (taken.Contains(id));
        var row = new ProfileRow
        {
            Profile = new WorkspaceSettings
            {
                Id = id,
                Name = name.Trim(),
                Color = NewProfileColors[_profiles.Count % NewProfileColors.Length],
                CloneRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code", name.Trim().ToLowerInvariant()),
                AutoFetchMinutes = _settings.AutoFetchMinutes,
            },
        };
        _profiles.Add(row);
        _editing = null; // nothing to keep: the editor still shows the previous profile
        RefreshProfileList(row);
        ProfileName.Focus();
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } row || _profiles.Count <= 1) return;
        Keep();
        var target = _profiles.First(r => r != row);
        if (row.Count > 0 && !PromptWindow.Confirm("Remove profile",
                $"Remove the profile '{row.Name}'?\n\nIts {row.CountText} move to '{target.Name}' (nothing on disk is touched).", "Remove", danger: true))
            return;
        _profiles.Remove(row);
        // repos of a profile removed earlier in this dialog follow along
        foreach (var k in _removed.Where(p => p.Value == row.Profile.Id).Select(p => p.Key).ToList()) _removed[k] = target.Profile.Id;
        _removed[row.Profile.Id] = target.Profile.Id;
        target.Count += row.Count;
        _editing = null;
        RefreshProfileList(target);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveProfile(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveProfile(+1);

    private void MoveProfile(int delta)
    {
        if (_editing is not { } row) return;
        var i = _profiles.IndexOf(row);
        var j = i + delta;
        if (j < 0 || j >= _profiles.Count) return;
        Keep();
        (_profiles[i], _profiles[j]) = (_profiles[j], _profiles[i]);
        RefreshProfileList(row);
    }

    /// <summary>The edited profiles become the settings' profiles; repos of removed ones move as shown.</summary>
    private void SaveProfiles()
    {
        Keep();
        _settings.Workspaces = _profiles.Select(r => r.Profile).ToList();
        foreach (var entry in _settings.Repos)
            if (entry.Workspace is { } id && _removed.TryGetValue(id, out var to)) entry.Workspace = to;
        if (_settings.ActiveWorkspace is { } active && _removed.TryGetValue(active, out var newActive)) _settings.ActiveWorkspace = newActive;
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void Color_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProfileSwatch is null) return;
        ProfileSwatch.Background = Workspaces.Brush(ProfileColor.Text);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => string.Empty };
        _settings.BranchPattern = string.IsNullOrWhiteSpace(PatternBox.Text) ? "feature/{name}-{user}" : PatternBox.Text.Trim();
        _settings.EditorCommand = string.IsNullOrWhiteSpace(EditorBox.Text) ? "code" : EditorBox.Text.Trim();
        _settings.TicketUrl = TicketUrlBox.Text.Trim();
        _settings.ExtraGitLabHosts = HostsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.Notifications = NotifyBox.IsChecked == true;
        _settings.MinimizeToTray = TrayBox.IsChecked == true;
        _settings.CompactDensity = CompactBox.IsChecked == true;

        SaveProfiles();
        _settings.IdentityCheck = SelectedTag(IdentityCheckBox) is { Length: > 0 } check ? check : "block";

        var name = NameBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        if (name.Length > 0 && email.Length > 0 && (name != _origName || email != _origEmail))
        {
            try
            {
                await RepoActions.SetGlobalIdentityAsync(name, email);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Settings were saved, but the global git identity could not be set:\n\n{ex.Message}",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        DialogResult = true;
    }

    private string? _exportPath;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export repos and settings",
            FileName = MigrationExport.DefaultFileName(),
            DefaultExt = ".json",
            Filter = "JSON file (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportButton.IsEnabled = false;
        OpenExportFolderButton.Visibility = Visibility.Collapsed;
        ExportWarnings.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => ExportStatus.Text = $"Reading repos... {p.Done} / {p.Total}");
            ExportStatus.Text = "Reading repos...";
            var file = await MigrationExport.BuildAsync(_settings, progress);
            await MigrationExport.WriteAsync(file, dialog.FileName);
            _exportPath = dialog.FileName;
            OpenExportFolderButton.Visibility = Visibility.Visible;

            var attention = file.Repos.Where(r => r.Warnings.Count > 0).ToList();
            ExportStatus.Text = attention.Count == 0
                ? $"Exported {file.RepoCount} repo(s). Nothing is left only on this computer."
                : $"Exported {file.RepoCount} repo(s). {attention.Count} have work that is only on this computer:";
            if (attention.Count > 0)
            {
                ExportWarnings.Text = string.Join(Environment.NewLine, attention.Select(r =>
                    $"{r.Name}  ({r.Path}){Environment.NewLine}" + string.Join(Environment.NewLine, r.Warnings.Select(w => "    - " + w))));
                ExportWarnings.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "Export failed.";
            MessageBox.Show(this, $"The export could not be written:\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private void OpenExportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_exportPath is null) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_exportPath}\"");
    }

    private string? _importFile;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import repos and settings",
            Filter = "repo-sync export (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;
        // the main window runs the import (it owns the repo list and the activity log)
        _importFile = dialog.FileName;
        DialogResult = false;
    }

    public static bool Edit(AppSettings settings, string? repoPath) => Edit(settings, repoPath, out _);

    /// <param name="importFile">Set when the user chose "Import from a file..." instead of saving.</param>
    /// <param name="profilesTab">Open on the Profiles tab (the sidebar's "Manage profiles").</param>
    public static bool Edit(AppSettings settings, string? repoPath, out string? importFile, bool profilesTab = false)
    {
        var w = new SettingsWindow(settings, repoPath, profilesTab);
        var saved = w.ShowDialog() == true;
        importFile = w._importFile;
        return saved;
    }
}
