using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Picks which repos of a migration file to clone / add, and per setting whether to keep this computer's value.</summary>
public partial class ImportWindow : Window
{
    private readonly List<ImportRepoItem> _repos;
    private readonly List<SettingDiffItem> _settings;

    private ImportWindow(MigrationFile file, List<ImportRepoItem> repos, List<SettingDiffItem> settings)
    {
        InitializeComponent();
        if (Application.Current.Dispatcher.CheckAccess())
            Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _repos = repos;
        _settings = settings;

        SourceText.Text = $"From {file.Machine} ({file.WindowsUser}), exported {file.ExportedAt:g} - {file.RepoCount} repo(s)";
        ReposGrid.ItemsSource = repos;
        ((CollectionViewSource)Resources["SettingsView"]).Source = settings;
        if (settings.Count == 0)
        {
            SettingsGrid.Visibility = Visibility.Collapsed;
            NoDiffText.Visibility = Visibility.Visible;
        }

        var after = new List<string>();
        if (file.AccountHosts.Count > 0) after.Add($"Add your tokens again under Accounts for: {string.Join(", ", file.AccountHosts)}.");
        if (file.SshKeys.Count > 0) after.Add($"Copy your SSH keys into ~/.ssh: {string.Join(", ", file.SshKeys)}.");
        AfterText.Text = after.Count == 0 ? string.Empty : "Not in the file: " + string.Join(" ", after);
        AfterText.Visibility = after.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var r in repos) r.PropertyChanged += (_, _) => UpdateSummary();
        foreach (var s in settings) s.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private (int Clone, int Add, int Take, int Keep) Counts() => (
        _repos.Count(r => r.Selected && r.Action == ImportAction.Clone),
        _repos.Count(r => r.Selected && r.Action == ImportAction.AddExisting),
        _settings.Count(s => s.Take),
        _settings.Count(s => s.CanTake && !s.Take));

    private void UpdateSummary()
    {
        var c = Counts();
        ReposTab.Header = $"Repositories ({c.Clone} to clone, {c.Add} to add)";
        SettingsTab.Header = $"Settings ({_settings.Count(s => s.CanTake)} differ)";
        SummaryText.Text = $"Clone {c.Clone}, add {c.Add}, take {c.Take} setting(s) from the file, keep {c.Keep} of this computer's.";
    }

    private void ReposAll_Click(object sender, RoutedEventArgs e) => _repos.ForEach(r => r.Selected = true);
    private void ReposNone_Click(object sender, RoutedEventArgs e) => _repos.ForEach(r => r.Selected = false);
    private void KeepAll_Click(object sender, RoutedEventArgs e) => _settings.ForEach(s => s.Take = false);
    private void TakeAll_Click(object sender, RoutedEventArgs e) => _settings.ForEach(s => s.Take = true);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // commit a folder still being typed in
        ReposGrid.CommitEdit();
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

        var c = Counts();
        if (c.Clone + c.Add + c.Take == 0)
        {
            MessageBox.Show(this, "Nothing is ticked - there is nothing to import.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = new List<string>();
        if (c.Clone > 0) lines.Add($"Clone {c.Clone} repo(s)");
        if (c.Add > 0) lines.Add($"Add {c.Add} repo folder(s) that are already here");
        if (c.Take > 0) lines.Add($"Overwrite {c.Take} setting(s) with the file's value: " +
                                  string.Join(", ", _settings.Where(s => s.Take).Select(s => s.Label).Take(8)) +
                                  (c.Take > 8 ? ", ..." : string.Empty));
        if (c.Keep > 0) lines.Add($"Keep this computer's value for {c.Keep} setting(s)");
        if (!PromptWindow.Confirm("Import", string.Join("\n\n", lines), "Import")) return;
        DialogResult = true;
    }

    public sealed record Plan(List<ImportRepoItem> Repos, List<SettingDiffItem> Settings);

    /// <summary>The ticked repos and settings, or null when cancelled.</summary>
    public static Plan? Ask(MigrationFile file, List<ImportRepoItem> repos, List<SettingDiffItem> settings)
    {
        var w = new ImportWindow(file, repos, settings);
        return w.ShowDialog() == true
            ? new Plan(repos.Where(r => r.Selected && r.CanImport).ToList(), settings.Where(s => s.Take).ToList())
            : null;
    }
}
