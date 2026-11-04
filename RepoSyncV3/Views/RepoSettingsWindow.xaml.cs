using System.Windows;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

public enum RepoIdentityChange
{
    Keep,
    UseWorkspace,
    UseGlobal,
    Custom,
}

/// <param name="Workspace">Id of the profile the repo belongs to.</param>
public sealed record RepoSettingsResult(string? DisplayName, string Workspace, RepoIdentityChange Identity, string GitName, string GitEmail,
    string? PrePushCommand, bool AutoFastForward, string? Color = null);

/// <summary>One repo's name in the list, its profile, and the identity its commits use.</summary>
public partial class RepoSettingsWindow : Window
{
    private readonly RepoViewModel _repo;

    private RepoSettingsWindow(RepoViewModel repo)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _repo = repo;
        HeadingText.Text = $"{repo.Name} settings";
        PathText.Text = repo.Remote is null ? repo.Path : $"{repo.Path}  ·  {repo.Remote.ServerName}: {repo.Remote.ProjectPath}";
        NameBox.Text = repo.Entry.DisplayName ?? string.Empty;
        PaletteList.ItemsSource = RepoColors.Palette;
        ColorPreviewText.Text = repo.Monogram;
        ColorBox.Text = repo.Entry.Color ?? string.Empty;
        UpdateColorPreview();

        foreach (var w in Workspaces.All) ProfileBox.Items.Add(w);
        ProfileBox.SelectedItem = repo.Workspace;

        CurrentIdentityText.Text = repo.IdentityText + (repo.HasLocalIdentity ? "  (set in this repo)" : "  (from your global git config)");
        GlobalRadio.IsEnabled = repo.HasLocalIdentity;
        CustomName.Text = repo.GitName;
        CustomEmail.Text = repo.GitEmail;
        PrePushBox.Text = repo.Entry.PrePushCommand ?? string.Empty;
        AutoFfBox.IsChecked = repo.Entry.AutoFastForward;
        UpdateIdentity();
    }

    private WorkspaceSettings EffectiveWorkspace => ProfileBox.SelectedItem as WorkspaceSettings ?? _repo.Workspace;

    private string ChosenWorkspace => EffectiveWorkspace.Id;

    private void ColorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateColorPreview();

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex }) ColorBox.Text = hex;
    }

    /// <summary>The avatar as it will look: the typed color, else the automatic one.</summary>
    private void UpdateColorPreview()
    {
        if (ColorPreview is null) return;
        ColorPreview.Background = Workspaces.Brush(RepoColors.Resolve(NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : _repo.Name, ColorBox.Text));
    }

    private void Workspace_Changed(object sender, RoutedEventArgs e) => UpdateIdentity();

    private void Identity_Changed(object sender, RoutedEventArgs e)
    {
        if (CustomGrid is not null) CustomGrid.IsEnabled = CustomRadio.IsChecked == true;
    }

    /// <summary>Explains the identity against the profile picked right now, and offers its identity.</summary>
    private void UpdateIdentity()
    {
        if (WorkspaceIdRadio is null || MismatchBorder is null) return;
        var w = EffectiveWorkspace;
        var hasId = w.GitEmail.Trim().Length > 0;
        WorkspaceIdRadio.Content = hasId
            ? $"Use the {w.Name} identity: {(w.GitName.Length > 0 ? w.GitName : _repo.GitName)} <{w.GitEmail}>"
            : $"Use the {w.Name} identity (set it in Settings > Profiles first)";
        WorkspaceIdRadio.IsEnabled = hasId;
        var mismatch = hasId && !string.Equals(_repo.GitEmail, w.GitEmail, StringComparison.OrdinalIgnoreCase);
        MismatchBorder.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        MismatchText.Text = $"This doesn't match {w.Name} ({w.GitEmail}).";
        if (mismatch && KeepRadio.IsChecked == true) WorkspaceIdRadio.IsChecked = true;
        if (!mismatch && WorkspaceIdRadio.IsChecked == true) KeepRadio.IsChecked = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRadio.IsChecked == true && (CustomName.Text.Trim().Length == 0 || !CustomEmail.Text.Contains('@')))
        {
            PromptWindow.Confirm("Name and email needed", "Enter a name and an email address for this repo's commits.", "OK");
            return;
        }
        DialogResult = true;
    }

    public static RepoSettingsResult? Edit(RepoViewModel repo)
    {
        var w = new RepoSettingsWindow(repo);
        if (w.ShowDialog() != true) return null;
        var identity = w.WorkspaceIdRadio.IsChecked == true ? RepoIdentityChange.UseWorkspace
            : w.GlobalRadio.IsChecked == true ? RepoIdentityChange.UseGlobal
            : w.CustomRadio.IsChecked == true ? RepoIdentityChange.Custom
            : RepoIdentityChange.Keep;
        return new RepoSettingsResult(w.NameBox.Text.Trim().Length == 0 ? null : w.NameBox.Text.Trim(), w.ChosenWorkspace, identity,
            w.CustomName.Text.Trim(), w.CustomEmail.Text.Trim(),
            w.PrePushBox.Text.Trim().Length == 0 ? null : w.PrePushBox.Text.Trim(), w.AutoFfBox.IsChecked == true,
            RepoColors.Normalize(w.ColorBox.Text));
    }
}
