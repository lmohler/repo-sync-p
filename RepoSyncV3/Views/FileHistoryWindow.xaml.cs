using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

public partial class FileHistoryWindow : Window
{
    private readonly string _repoPath;
    private readonly string _relativePath;

    public FileHistoryWindow(string repoPath, string relativePath, List<CommitInfo> commits)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        _repoPath = repoPath;
        _relativePath = relativePath;
        FileText.Text = relativePath;
        CommitGrid.ItemsSource = commits;
        if (commits.Count > 0)
        {
            CommitGrid.SelectedIndex = 0;
        }
    }

    private async void CommitGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadDiffAsync();
    }

    private async void IgnoreWs_Click(object sender, RoutedEventArgs e)
    {
        await LoadDiffAsync();
    }

    private async Task LoadDiffAsync()
    {
        if (CommitGrid.SelectedItem is not CommitInfo commit)
        {
            DiffList.ItemsSource = null;
            return;
        }
        var ignoreWs = IgnoreWsBox.IsChecked == true;
        try
        {
            var diff = await Git.GetCommitDiffAsync(_repoPath, commit.Hash, _relativePath, ignoreWs);
            // clicking quickly through commits: only the latest selection's diff is shown
            if (ReferenceEquals(CommitGrid.SelectedItem, commit) && IgnoreWsBox.IsChecked == ignoreWs) DiffList.ItemsSource = diff;
        }
        catch (Exception ex)
        {
            DiffList.ItemsSource = null;
            MessageBox.Show(this, $"Could not load the diff for {commit.Hash[..Math.Min(8, commit.Hash.Length)]}:\n\n{ex.Message}",
                "File history", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (CommitGrid.SelectedItem is CommitInfo c)
        {
            Clipboard.SetText(c.Hash);
        }
    }
}

