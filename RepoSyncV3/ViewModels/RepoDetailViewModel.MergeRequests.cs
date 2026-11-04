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
    // =================================================================================== merge requests

    public ObservableCollection<ForgeMergeRequest> MergeRequests { get; } = new();
    public IReadOnlyList<string> MergeRequestScopes { get; } = new[] { "Mine", "To review", "All open", "Merged" };

    private string _mergeRequestScope = "All open";
    private ForgeMergeRequest? _selectedMergeRequest;
    private ForgeMergeRequest? _mergeRequestDetail;
    private string? _serverMessage;
    private bool _squashOnMerge;
    private bool _removeSourceOnMerge = true;

    public string MergeRequestScope
    {
        get => _mergeRequestScope;
        set
        {
            if (Set(ref _mergeRequestScope, value)) _ = LoadMergeRequestsAsync();
        }
    }

    public ForgeMergeRequest? SelectedMergeRequest
    {
        get => _selectedMergeRequest;
        set
        {
            if (Set(ref _selectedMergeRequest, value)) _ = LoadMergeRequestDetailAsync();
        }
    }

    /// <summary>The selected merge request fetched on its own (that call includes its head pipeline).</summary>
    public ForgeMergeRequest? MergeRequestDetail { get => _mergeRequestDetail; private set => Set(ref _mergeRequestDetail, value); }

    /// <summary>Shown instead of the server's lists when there is no GitHub / GitLab remote or no token.</summary>
    public string? ServerMessage { get => _serverMessage; private set => Set(ref _serverMessage, value); }

    public bool SquashOnMerge { get => _squashOnMerge; set => Set(ref _squashOnMerge, value); }
    public bool RemoveSourceOnMerge { get => _removeSourceOnMerge; set => Set(ref _removeSourceOnMerge, value); }

    private IForge? ClientOrMessage()
    {
        if (Repo.Remote is null)
        {
            ServerMessage = "This repo's remote isn't on github.com or a GitLab server. (If it is a self-hosted GitLab whose name doesn't contain \"gitlab\", add the host in Settings.)";
            return null;
        }
        if (Forges.For(Repo.Remote) is not { } client)
        {
            ServerMessage = $"No {Repo.ServerName} token saved for {Repo.Remote.Host}. Click the key button (Accounts) in the toolbar to add one.";
            return null;
        }
        ServerMessage = null;
        return client;
    }

    private async Task LoadMergeRequestsAsync()
    {
        if (ClientOrMessage() is not { } client) return;
        var remote = Repo.Remote!;
        var scope = _mergeRequestScope switch
        {
            "Mine" => Models.MergeRequestScope.Mine,
            "To review" => Models.MergeRequestScope.ToReview,
            "Merged" => Models.MergeRequestScope.Merged,
            _ => Models.MergeRequestScope.AllOpen,
        };
        var list = await _main.RunApiAsync(Repo.MrsTerm, () => client.ListMergeRequestsAsync(remote, scope));
        if (list is null) return;
        var keep = SelectedMergeRequest?.Iid;
        Replace(MergeRequests, list);
        SelectedMergeRequest = MergeRequests.FirstOrDefault(m => m.Iid == keep)
                               ?? MergeRequests.FirstOrDefault(m => m.SourceBranch == Repo.Branch)
                               ?? MergeRequests.FirstOrDefault();
    }

    private async Task LoadMergeRequestDetailAsync()
    {
        MergeRequestDetail = null;
        if (SelectedMergeRequest is not { } mr || Repo.Remote is null || Forges.For(Repo.Remote) is not { } client) return;
        try
        {
            var detail = await client.GetMergeRequestAsync(Repo.Remote, mr.Iid);
            if (SelectedMergeRequest?.Iid == mr.Iid) MergeRequestDetail = detail;
        }
        catch (Exception ex)
        {
            _main.AddLog($"{mr.Reference}: {ex.Message}", LogLevel.Warning);
            MergeRequestDetail = mr;
        }
    }

    private async Task ApproveAsync(object? _)
    {
        var mr = SelectedMergeRequest!;
        if (!ServerWriteNotice.Confirm("approve", "Approve")) return;
        var client = Forges.Require(Repo.Remote!);
        if (!await _main.RunApiAsync("Approve", () => client.ApproveAsync(Repo.Remote!, mr))) return;
        _main.AddLog($"Approved {mr.Reference}.", LogLevel.Success);
        _main.ShowBanner($"Approved {mr.Reference}", mr.Title, "success");
        await LoadMergeRequestDetailAsync();
    }

    private async Task MergeAsync(object? _)
    {
        var mr = SelectedMergeRequest!;
        var how = (SquashOnMerge ? "squash and merge" : "merge") + (RemoveSourceOnMerge ? ", then delete the source branch" : string.Empty);
        if (!PromptWindow.Confirm("Merge", $"Merge {mr.Reference} \"{mr.Title}\" ({mr.SourceBranch} into {mr.TargetBranch}) - {how}?", "Merge")) return;
        if (!ServerWriteNotice.Confirm("merge", "Merge")) return;
        var client = Forges.Require(Repo.Remote!);
        if (!await _main.RunApiAsync("Merge", () => client.MergeAsync(Repo.Remote!, mr, SquashOnMerge, RemoveSourceOnMerge))) return;
        _main.AddLog($"Merged {mr.Reference} into {mr.TargetBranch}.", LogLevel.Success);
        _main.ShowBanner($"Merged {mr.Reference}",
            mr.SourceBranch == Repo.Branch ? "Click Finish branch to switch to the default branch and clean up." : mr.Title, "success");
        await _main.RefreshRepoAsync(Repo);
    }

    private async Task ToggleDraftAsync(object? _)
    {
        var mr = SelectedMergeRequest!;
        var client = Forges.Require(Repo.Remote!);
        if (!await _main.RunApiAsync($"Update {Repo.MrTerm}", () => client.SetDraftAsync(Repo.Remote!, mr, !mr.Draft))) return;
        _main.AddLog(!mr.Draft ? $"{mr.Reference} marked as draft." : $"{mr.Reference} marked as ready.", LogLevel.Success);
        await LoadMergeRequestsAsync();
    }
}
