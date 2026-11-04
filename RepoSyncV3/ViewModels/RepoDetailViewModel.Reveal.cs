using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

public sealed partial class RepoDetailViewModel
{
    // =================================================================================== reveal (global search)

    /// <summary>
    /// Opens the tab a Search all repos result lives on and selects it there: a commit on History (widening the
    /// scope to all branches, or showing just that commit, when it isn't in the loaded list), a branch on Branches,
    /// a merge request on Merge requests (in the Merged list when it was found there).
    /// </summary>
    public async Task RevealAsync(GlobalHit hit)
    {
        switch (hit.Kind)
        {
            case HitKind.Commit:
                ShowTab(TabHistory);
                await LoadHistoryAsync();
                if (FindCommit(hit.Key) is null && _historyScope != "all")
                {
                    _historyScope = "all";
                    OnPropertyChanged(nameof(HistoryScope));
                    await LoadHistoryAsync();
                }
                if (FindCommit(hit.Key) is null)
                {
                    // older than the loaded list: show just that commit, like a history search result
                    _searchResults = await Git.GetLogAsync(Repo.Path, hit.Key, 1);
                    ApplyHistoryFilter();
                }
                SelectedCommit = FindCommit(hit.Key) ?? SelectedCommit;
                break;

            case HitKind.Branch:
                ShowTab(TabBranches);
                await LoadBranchesAsync();
                if (LocalBranches.FirstOrDefault(b => b.Name == hit.Key) is { } local)
                {
                    SelectedLocalBranch = local;
                }
                else
                {
                    RemoteFilter = string.Empty;
                    SelectedRemoteBranch = RemoteBranches.FirstOrDefault(b => b.Name == hit.Key);
                }
                break;

            case HitKind.MergeRequest:
                ShowTab(TabMergeRequests);
                var scope = hit.Merged ? "Merged" : "All open";
                if (_mergeRequestScope != scope)
                {
                    _mergeRequestScope = scope;
                    OnPropertyChanged(nameof(MergeRequestScope));
                }
                await LoadMergeRequestsAsync();
                SelectedMergeRequest = MergeRequests.FirstOrDefault(m => m.Iid.ToString() == hit.Key) ?? SelectedMergeRequest;
                break;
        }
    }

    private CommitInfo? FindCommit(string hash) => Commits.FirstOrDefault(c => c.Hash == hash);

    /// <summary>Switches tab without SelectedTab's own load (RevealAsync loads and then selects).</summary>
    private void ShowTab(int tab)
    {
        if (_selectedTab == tab) return;
        _selectedTab = tab;
        _pipelinePoll.Stop();
        OnPropertiesChanged(nameof(SelectedTab), nameof(ShowTagsTab), nameof(ShowWorktreesTab));
    }
}
