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
    // =================================================================================== stashes

    public ObservableCollection<StashInfo> Stashes { get; } = new();
    private StashInfo? _selectedStash;
    private List<DiffLine> _stashDiff = new();

    public StashInfo? SelectedStash
    {
        get => _selectedStash;
        set
        {
            if (Set(ref _selectedStash, value)) _ = LoadStashDiffAsync();
        }
    }

    public List<DiffLine> StashDiff { get => _stashDiff; private set => Set(ref _stashDiff, value); }

    private async Task LoadStashesAsync()
    {
        Replace(Stashes, await Git.GetStashesAsync(Repo.Path));
        StashCount = Stashes.Count;
        SelectedStash = Stashes.FirstOrDefault();
        if (SelectedStash is null) StashDiff = new List<DiffLine>();
    }

    private async Task LoadStashDiffAsync()
    {
        StashDiff = SelectedStash is null ? new List<DiffLine>() : await Git.GetStashDiffAsync(Repo.Path, SelectedStash.Name, IgnoreWhitespace);
    }

    private async Task StashAsync(object? _)
    {
        var message = PromptWindow.Ask("Stash changes", "Put your uncommitted changes aside (new files included). Optional note:", string.Empty, "Stash");
        if (message is null) return;
        await Run("Stash", a => a.StashAsync(message, includeUntracked: true), "Changes stashed");
    }
}
