using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Your commits across the workspace's repos since the last workday / today / last week, to paste into a standup.</summary>
public partial class StandupWindow : Window
{
    private readonly IReadOnlyList<(string Name, string Path, IReadOnlyCollection<string> Emails)> _repos;

    private StandupWindow(string scope, IReadOnlyList<(string Name, string Path, IReadOnlyCollection<string> Emails)> repos)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _repos = repos;
        ScopeText.Text = $"Your commits on every local branch, {scope} ({repos.Count} repos). Matched by each repo's commit email and its profile email.";
        Loaded += async (_, _) => await LoadAsync();
    }

    private DateTime Since =>
        TodayRadio.IsChecked == true ? DateTime.Today
        : WeekRadio.IsChecked == true ? DateTime.Today.AddDays(-7)
        : Standup.LastWorkday(DateTime.Today);

    private async void Range_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var since = Since;
        OutputBox.Text = "Reading commits...";
        CopyButton.IsEnabled = false;
        var commits = await Standup.CollectAsync(_repos, since);
        OutputBox.Text = Standup.Format(commits, since);
        CopyButton.IsEnabled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OutputBox.Text);
        CopyButton.Content = "Copied";
    }

    public static void Show(string scope, IReadOnlyList<(string Name, string Path, IReadOnlyCollection<string> Emails)> repos) =>
        new StandupWindow(scope, repos).ShowDialog();
}
