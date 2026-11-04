using System.IO;
using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

public sealed record CloneChoice(string Url, string Target, string Workspace);

public partial class CloneWindow : Window
{
    // public: WPF bindings only see properties of public types
    public sealed record Account(string Host, ForgeKind Kind, string Label);

    private readonly AppSettings _settings;
    private List<ForgeProject> _projects = new();
    private bool _urlFromList;
    private bool _rootTouched;
    private bool _settingRoot;

    private CloneWindow(AppSettings settings, string profile)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _settings = settings;

        foreach (var w in settings.Workspaces) WorkspaceBox.Items.Add(w);
        SelectWorkspace(profile);
        foreach (var h in TokenStore.Hosts)
            HostBox.Items.Add(new Account(h, Forges.KindOfHost(h), $"{h}  ({(ForgeLinks.IsGitHubHost(h) ? "GitHub" : "GitLab")})"));

        if (HostBox.Items.Count > 0)
            HostBox.SelectedIndex = 0;
        else
            ListMessage.Text = "No GitHub or GitLab token saved yet - add one with the key button (Accounts) in the toolbar to browse your projects.\nYou can still paste a clone URL below.";
        UpdateTarget();
    }

    private WorkspaceSettings? SelectedWorkspace => WorkspaceBox.SelectedItem as WorkspaceSettings;

    private void SelectWorkspace(string id) =>
        WorkspaceBox.SelectedItem = _settings.Workspaces.FirstOrDefault(w => w.Id == id) ?? _settings.Workspaces[0];

    private void Workspace_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rootTouched || SelectedWorkspace is not { } w) return;
        _settingRoot = true;
        RootBox.Text = string.IsNullOrWhiteSpace(w.CloneRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code", w.Name.ToLowerInvariant())
            : w.CloneRoot;
        _settingRoot = false;
        UpdateTarget();
    }

    private async void Host_Changed(object sender, SelectionChangedEventArgs e) => await LoadProjectsAsync();

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadProjectsAsync();

    private async Task LoadProjectsAsync()
    {
        if (HostBox.SelectedItem is not Account a || Forges.For(a.Kind, a.Host) is not { } client) return;
        ListMessage.Text = "Loading your projects...";
        ProjectList.ItemsSource = null;
        try
        {
            _projects = await client.ListMyProjectsAsync();
            ListMessage.Text = _projects.Count == 0 ? "You don't have access to any project on this server." : string.Empty;
            ApplyFilter();
        }
        catch (UserFacingException ex)
        {
            ListMessage.Text = ex.Message + "\n" + string.Join("\n", ex.Help);
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        ProjectList.ItemsSource = _projects
            .Where(p => f.Length == 0 || p.PathWithNamespace.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || (p.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    private void Project_Changed(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ForgeProject p) return;
        _urlFromList = true;
        UrlBox.Text = SshBox.IsChecked == true ? p.SshUrlToRepo : p.HttpUrlToRepo;
        _urlFromList = false;
        UpdateTarget();
    }

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_urlFromList && sender == UrlBox) ProjectList.SelectedItem = null;
        if (sender == RootBox && !_settingRoot) _rootTouched = true;
        UpdateTarget();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to clone into", InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null };
        if (dialog.ShowDialog(this) == true) RootBox.Text = dialog.FolderName;
    }

    private string? Target
    {
        get
        {
            var url = UrlBox.Text.Trim();
            if (url.Length == 0 || RootBox.Text.Trim().Length == 0) return null;
            var name = url.TrimEnd('/');
            name = name[(name.LastIndexOfAny(new[] { '/', ':' }) + 1)..];
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return name.Length == 0 ? null : Path.Combine(RootBox.Text.Trim(), name);
        }
    }

    private void UpdateTarget()
    {
        if (TargetText is null || OkButton is null) return;
        var target = Target;
        if (target is null)
        {
            TargetText.Text = "Choose a project or paste a URL.";
            OkButton.IsEnabled = false;
        }
        else if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            TargetText.Text = $"{target} already exists and isn't empty.";
            OkButton.IsEnabled = false;
        }
        else
        {
            var w = SelectedWorkspace;
            TargetText.Text = $"Will clone into {target}" + (w is { GitEmail.Length: > 0 } ? $" and commit as {w.GitEmail}" : string.Empty);
            OkButton.IsEnabled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <param name="profile">Profile picked at first (the one loaded in the sidebar).</param>
    public static CloneChoice? Ask(AppSettings settings, string profile)
    {
        var w = new CloneWindow(settings, profile);
        if (w.ShowDialog() != true || w.Target is not { } target || w.SelectedWorkspace is not { } workspace) return null;
        workspace.CloneRoot = w.RootBox.Text.Trim();
        return new CloneChoice(w.UrlBox.Text.Trim(), target, workspace.Id);
    }
}
