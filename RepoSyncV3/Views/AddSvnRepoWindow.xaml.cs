using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <param name="SvnUrl">Null = the folder already is a working copy.</param>
public sealed record SvnSetupRequest(string? SvnUrl, string WorkingCopyPath, string WorkBranch, string WorktreePath, string Workspace);

/// <summary>Asks for what <see cref="SvnWorkflow.SetupAsync"/> needs. The working copy folder is suggested from the URL,
/// and the work folder from the working copy ("&lt;folder&gt;-&lt;branch&gt;"), until you type in those boxes yourself.
/// Ported from git-sync-v2's Add SVN Repo.</summary>
public partial class AddSvnRepoWindow : Window
{
    private readonly string _parentFolder;
    private bool _folderEdited;
    private bool _worktreeEdited;
    private bool _updating;

    private AddSvnRepoWindow(string parentFolder, string profile)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _parentFolder = parentFolder;
        foreach (var w in Workspaces.All) ProfileBox.Items.Add(w);
        ProfileBox.SelectedItem = Workspaces.Get(profile);
        Loaded += (_, _) => UrlBox.Focus();
    }

    public SvnSetupRequest? Result { get; private set; }

    /// <summary>Shows the dialog; returns the request, or null if cancelled.</summary>
    /// <param name="profile">Profile picked at first (the one loaded in the sidebar).</param>
    public static SvnSetupRequest? Ask(string parentFolder, string profile)
    {
        var w = new AddSvnRepoWindow(parentFolder, profile);
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void SetText(TextBox box, string text)
    {
        _updating = true;
        box.Text = text;
        _updating = false;
    }

    /// <summary>".../svn/my-repo/trunk" -> "my-repo"; ".../branches/feature-x" -> "feature-x".</summary>
    public static string? NameFromUrl(string url)
    {
        var parts = url.Trim().TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var name = parts[^1];
        if (string.Equals(name, "trunk", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3) name = parts[^2];
        name = Uri.UnescapeDataString(name);
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    private void SuggestWorktree()
    {
        if (_worktreeEdited || WorktreeBox is null) return;
        var folder = FolderBox.Text.Trim().TrimEnd('\\', '/');
        var branch = BranchBox.Text.Trim();
        SetText(WorktreeBox, folder.Length > 0 && branch.Length > 0 ? $"{folder}-{branch}" : string.Empty);
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_folderEdited || NameFromUrl(UrlBox.Text) is not { } name) return;
        SetText(FolderBox, Path.Combine(_parentFolder, name));
        SuggestWorktree();
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) _folderEdited = FolderBox.Text.Length > 0;
        SuggestWorktree();
    }

    private void BranchBox_TextChanged(object sender, TextChangedEventArgs e) => SuggestWorktree();

    private void WorktreeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) _worktreeEdited = WorktreeBox.Text.Length > 0;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "SVN working copy folder (existing, or empty for a new checkout)", InitialDirectory = _parentFolder };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void BrowseWorktree_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Work folder (must be empty or not exist yet)", InitialDirectory = _parentFolder };
        if (dialog.ShowDialog(this) == true) WorktreeBox.Text = dialog.FolderName;
    }

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The error to show, or null with <paramref name="request"/> filled in.</summary>
    public static string? Validate(string url, string folder, string branch, string worktree, string workspace, out SvnSetupRequest? request)
    {
        request = null;
        url = url.Trim();
        folder = folder.Trim();
        branch = branch.Trim();
        worktree = worktree.Trim();

        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _)) return "The SVN URL isn't a valid URL.";
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) return "Enter the full path of the working copy folder.";
        if (branch.Length == 0) return "Enter the work branch name.";
        if (worktree.Length == 0 || !Path.IsPathFullyQualified(worktree)) return "Enter the full path of the work folder.";

        folder = Path.GetFullPath(folder).TrimEnd('\\');
        worktree = Path.GetFullPath(worktree).TrimEnd('\\');
        if (IsSameOrInside(worktree, folder) || IsSameOrInside(folder, worktree))
            return "The work folder must be outside the working copy (SVN would see it as a new folder), e.g. next to it.";

        var isWorkingCopy = Svn.IsWorkingCopy(folder);
        if (!isWorkingCopy && url.Length == 0)
            return "That folder isn't an SVN working copy. Enter the SVN URL to check out into it, or pick an existing working copy.";
        if (!isWorkingCopy && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return "That folder isn't an SVN working copy and isn't empty, so SVN can't check out into it.";

        request = new SvnSetupRequest(url.Length > 0 ? url : null, folder, branch, worktree, workspace);
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var workspace = (ProfileBox.SelectedItem as WorkspaceSettings ?? Workspaces.All[0]).Id;
        var error = Validate(UrlBox.Text, FolderBox.Text, BranchBox.Text, WorktreeBox.Text, workspace, out var request);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorBorder.Visibility = Visibility.Visible;
            return;
        }
        Result = request;
        DialogResult = true;
    }
}
