using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Services;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

public partial class StartBranchWindow : Window
{
    private readonly string _user;

    private StartBranchWindow(RepoViewModel repo, string pattern, string user, IReadOnlyList<string> localBranches)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _user = user;
        RepoText.Text = $"{repo.Name} - currently on '{repo.Branch}'";
        PatternBox.Text = pattern;
        if (repo.RemoteName is not null)
        {
            StartPointBox.Items.Add(repo.DefaultRef + "  (latest from the server - usual choice)");
            if (!repo.Detached && repo.Branch.Length > 0) StartPointBox.Items.Add(repo.Branch + "  (the branch you're on)");
        }
        else
        {
            // No remote (e.g. an SVN working copy's local git): branch from what's here.
            NoRemoteNote.Visibility = Visibility.Visible;
            StartPointBox.Items.Add(repo.Detached || repo.Branch.Length == 0 ? "HEAD  (the commit you're on)" : repo.Branch + "  (the branch you're on)");
            foreach (var b in localBranches.Where(b => b != repo.Branch)) StartPointBox.Items.Add(b);
        }
        StartPointBox.SelectedIndex = 0;
        DirtyNote.Visibility = repo.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        PublishBox.Visibility = repo.HasForge ? Visibility.Visible : Visibility.Collapsed;
        PublishBox.IsChecked = repo.HasForge;
        PublishBox.Content = $"Publish to {repo.ServerName} right away (push -u)";
        Loaded += (_, _) => NameBox.Focus();
        UpdatePreview();
    }

    private string BranchName => RepoActions.BuildBranchName(PatternBox.Text, NameBox.Text, _user);

    private string StartPoint => ((string)StartPointBox.SelectedItem).Split("  (")[0];

    private void Inputs_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        PreviewText.Text = NameBox.Text.Trim().Length == 0 ? "(type a name above)" : BranchName;
        OkButton.IsEnabled = NameBox.Text.Trim().Length > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static (string Branch, string StartPoint, bool Publish, string Pattern)? Ask(RepoViewModel repo, ref string pattern, string user,
        IReadOnlyList<string>? localBranches = null)
    {
        var w = new StartBranchWindow(repo, pattern, user, localBranches ?? Array.Empty<string>());
        if (w.ShowDialog() != true) return null;
        pattern = w.PatternBox.Text;
        return (w.BranchName, w.StartPoint, w.PublishBox.IsChecked == true, w.PatternBox.Text);
    }
}
