using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

public sealed partial class MainViewModel
{
    // =================================================================================== global search

    /// <summary>A Search all repos result was opened: select its repo and show the item on its tab.</summary>
    public async void RevealSearchHit(GlobalHit hit)
    {
        if (Repos.FirstOrDefault(r => string.Equals(r.Path, hit.RepoPath, StringComparison.OrdinalIgnoreCase)) is not { } repo) return;
        SelectedRepo = repo;
        try
        {
            if (Detail is { } d && d.Repo == repo) await d.RevealAsync(hit);
        }
        catch (Exception ex)
        {
            AddLog($"Could not open {hit.Title}: {ex.Message}", LogLevel.Error);
        }
    }
}