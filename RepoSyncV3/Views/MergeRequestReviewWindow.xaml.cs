using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Reads a merge / pull request in the app: its diff (computed by local git from the fetched
/// branches, so it costs no API calls) and its comments, with a box to add one.</summary>
public partial class MergeRequestReviewWindow : Window
{
    private sealed record ChangedFile(string Code, string Path);

    private readonly string _repoPath;
    private readonly string _remoteName;
    private readonly ForgeRemote _remote;
    private readonly ForgeMergeRequest _mr;
    private string _range = string.Empty;

    public MergeRequestReviewWindow(string repoPath, string remoteName, ForgeRemote remote, ForgeMergeRequest mr)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        _repoPath = repoPath;
        _remoteName = remoteName;
        _remote = remote;
        _mr = mr;
        TitleText.Text = $"{mr.Reference}  {mr.Title}";
        SubText.Text = $"{mr.SourceBranch} into {mr.TargetBranch}";
        NewComment.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Post_Click(this, new RoutedEventArgs()); }
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // the source branch may not be fetched yet (someone else's MR)
            await Git.RunAsync(_repoPath, new[] { "fetch", _remoteName, _mr.SourceBranch, _mr.TargetBranch }, timeout: Git.NetworkTimeout);
            _range = $"{_remoteName}/{_mr.TargetBranch}...{_remoteName}/{_mr.SourceBranch}";
            var r = await Git.RunAsync(_repoPath, new[] { "diff", "--name-status", "-M", _range });
            if (!r.Success)
            {
                // GitHub forks: the head branch isn't on this remote - fall back to the pull ref
                if (_remote.IsGitHub && (await Git.RunAsync(_repoPath, new[] { "fetch", _remoteName, $"pull/{_mr.Iid}/head:refs/remotes/{_remoteName}/pr-{_mr.Iid}" }, timeout: Git.NetworkTimeout)).Success)
                {
                    _range = $"{_remoteName}/{_mr.TargetBranch}...{_remoteName}/pr-{_mr.Iid}";
                    r = await Git.RunAsync(_repoPath, new[] { "diff", "--name-status", "-M", _range });
                }
            }
            var files = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(p => p.Length >= 2)
                .Select(p => new ChangedFile(p[0][..1], p[^1])).ToList();
            Files.ItemsSource = files;
            SubText.Text += $"  ·  {files.Count} file(s)";
            if (files.Count > 0) Files.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SubText.Text += $"  ·  diff unavailable: {ex.Message}";
        }
        await LoadCommentsAsync();
    }

    private async Task LoadCommentsAsync()
    {
        try
        {
            Comments.ItemsSource = await Forges.Require(_remote).ListCommentsAsync(_remote, _mr);
            CommentScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            Comments.ItemsSource = new[] { new ForgeComment("(error)", ex.Message, null, null, null) };
        }
    }

    private async void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Files.SelectedItem is not ChangedFile f) return;
        var r = await Git.RunAsync(_repoPath, new[] { "diff", "-M", _range, "--", f.Path });
        if (ReferenceEquals(Files.SelectedItem, f)) Diff.ItemsSource = Git.ParseDiff(r.StdOut);
    }

    /// <summary>Double-click a diff line: quote it (file:line + code) into the comment box to comment on it.</summary>
    private void Diff_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Diff.SelectedItem is not DiffLine { Kind: DiffKind.Context or DiffKind.Add or DiffKind.Delete } line
            || Files.SelectedItem is not ChangedFile f) return;
        var no = line.NewNo.Length > 0 ? line.NewNo : line.OldNo;
        var code = line.Text.Length > 0 ? line.Text[1..] : string.Empty; // drop the +/-/space diff prefix
        var quote = $"`{f.Path}:{no}`\n```\n{code}\n```\n";
        NewComment.Text = (NewComment.Text.Trim().Length > 0 ? NewComment.Text.TrimEnd() + "\n\n" : string.Empty) + quote;
        NewComment.Focus();
        NewComment.CaretIndex = NewComment.Text.Length;
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        var body = NewComment.Text.Trim();
        if (body.Length == 0 || !ServerWriteNotice.Confirm("comment", "Post a comment")) return;
        try
        {
            IsEnabled = false;
            await Forges.Require(_remote).AddCommentAsync(_remote, _mr, body);
            NewComment.Clear();
            await LoadCommentsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Post comment", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Web_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(_mr.WebUrl);
}
