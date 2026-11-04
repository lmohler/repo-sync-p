using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class RepoDetailViewModel
{
    // =================================================================================== branches

    public ObservableCollection<TagInfo> Tags { get; } = new();
    private TagInfo? _selectedTag;
    public TagInfo? SelectedTag { get => _selectedTag; set => Set(ref _selectedTag, value); }

    public async Task LoadTagsAsync()
    {
        if (!Repo.Loaded || !Directory.Exists(Repo.Path)) return;
        var tags = await Git.GetTagsAsync(Repo.Path);
        Replace(Tags, tags);
        TagCount = tags.Count;
    }

    public ObservableCollection<BranchInfo> LocalBranches { get; } = new();
    public ObservableCollection<BranchInfo> RemoteBranches { get; } = new();
    private List<BranchInfo> _allRemote = new();
    private BranchInfo? _selectedLocalBranch;
    private BranchInfo? _selectedRemoteBranch;
    private string _remoteFilter = string.Empty;

    public BranchInfo? SelectedLocalBranch { get => _selectedLocalBranch; set => Set(ref _selectedLocalBranch, value); }
    public BranchInfo? SelectedRemoteBranch { get => _selectedRemoteBranch; set => Set(ref _selectedRemoteBranch, value); }

    public string RemoteFilter
    {
        get => _remoteFilter;
        set
        {
            if (Set(ref _remoteFilter, value)) ApplyRemoteFilter();
        }
    }

    public int CleanupCandidates => LocalBranches.Count(IsCleanupCandidate);
    public string CleanupText => CleanupCandidates > 0 ? $"Clean up ({CleanupCandidates})" : "Clean up";

    private bool IsCleanupCandidate(BranchInfo b) =>
        !b.IsCurrent && b.Name != Repo.DefaultBranch && (b.IsMerged || b.UpstreamGone);

    private async Task LoadBranchesAsync()
    {
        var local = await Git.GetLocalBranchesAsync(Repo.Path);
        if (Repo.RemoteName is { } remote)
        {
            var merged = await Git.GetMergedBranchesAsync(Repo.Path, Repo.DefaultRef);
            foreach (var b in local) b.IsMerged = merged.Contains(b.Name) && b.Name != Repo.DefaultBranch;
            var localNames = local.Select(b => b.Name).ToHashSet();
            _allRemote = await Git.GetRemoteBranchesAsync(Repo.Path, remote);
            foreach (var b in _allRemote) b.HasLocal = localNames.Contains(b.Name[(remote.Length + 1)..]);
        }
        Replace(LocalBranches, local);
        ApplyRemoteFilter();
        await LoadTagsAsync();
        OnPropertiesChanged(nameof(CleanupCandidates), nameof(CleanupText));
    }

    private void ApplyRemoteFilter() =>
        Replace(RemoteBranches, _allRemote.Where(b => _remoteFilter.Length == 0 || b.Name.Contains(_remoteFilter, StringComparison.OrdinalIgnoreCase)
                                                    || b.Author.Contains(_remoteFilter, StringComparison.OrdinalIgnoreCase)));

    private async Task DeleteBranchAsync(object? _)
    {
        var b = SelectedLocalBranch!;
        var force = !b.IsMerged;
        var text = b.IsMerged
            ? $"Delete local branch '{b.Name}'? Its work is already in {Repo.DefaultBranch}."
            : $"'{b.Name}' has commits that aren't in {Repo.DefaultBranch}. Delete it anyway? Those commits will be lost (unless they are on the server).";
        if (!PromptWindow.Confirm("Delete branch", text, "Delete", danger: force)) return;
        await Run("Delete branch", a => a.DeleteBranchAsync(b.Name, force), $"Deleted '{b.Name}'");
    }

    private async Task DeleteRemoteBranchAsync(object? _)
    {
        var b = SelectedRemoteBranch!;
        if (!PromptWindow.Confirm("Delete branch on the server", $"Delete '{b.Name}' on {Repo.ServerName} for everyone?", "Delete on server", danger: true)) return;
        await Run("Delete server branch", a => a.DeleteRemoteBranchAsync(b.Name), $"Deleted '{b.Name}' on the server");
    }

    private async Task RenameBranchAsync(object? _)
    {
        var b = SelectedLocalBranch!;
        var name = PromptWindow.Ask("Rename branch", $"New name for '{b.Name}' (local only):", b.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == b.Name) return;
        await Run("Rename branch", a => a.RenameBranchAsync(b.Name, name.Trim()), $"Renamed to '{name.Trim()}'");
    }

    private async Task CleanupBranchesAsync(object? _)
    {
        if (LocalBranches.Count == 0) await LoadBranchesAsync();
        var candidates = LocalBranches.Where(IsCleanupCandidate).Select(b => b.Name).ToList();
        if (candidates.Count == 0)
        {
            _main.ShowBanner("Nothing to clean up", "No local branch is merged or deleted on the server.", "success");
            return;
        }
        var chosen = ScanResultsWindow.Ask(candidates, "Clean up branches",
            "These local branches are merged into the default branch, or were deleted on the server. Delete the ticked ones:", "Delete branches");
        if (chosen is null || chosen.Count == 0) return;
        await _main.RunRepoAsync(Repo, "Clean up branches", async a => $"Deleted {await a.CleanupMergedAsync(chosen)} branch(es)", refreshServer: false);
    }
}
