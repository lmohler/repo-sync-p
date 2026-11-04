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
    // =================================================================================== overview

    public ObservableCollection<CommitInfo> RecentCommits { get; } = new();
    public ObservableCollection<ForgeJob> FailedJobs { get; } = new();
    private int _stashCount;
    public int StashCount { get => _stashCount; private set { if (Set(ref _stashCount, value)) OnPropertyChanged(nameof(ShowStashesTab)); } }

    private async Task LoadOverviewAsync()
    {
        if (!Repo.Loaded || !Directory.Exists(Repo.Path)) return;
        var commits = await Git.GetLogAsync(Repo.Path, "HEAD", 6);
        if (Repo.HasUpstream)
        {
            var unpushed = await Git.GetUnpushedHashesAsync(Repo.Path, Repo.Upstream!);
            foreach (var c in commits) c.IsUnpushed = unpushed.Contains(c.Hash);
        }
        Replace(RecentCommits, commits);
        StashCount = (await Git.GetStashesAsync(Repo.Path)).Count;
        LinkedWorktreeCount = (await Git.GetWorktreesAsync(Repo.Path)).Count(w => !w.IsMain);
        TagCount = (await Git.RunAsync(Repo.Path, new[] { "tag", "--list" })).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        FailedJobs.Clear();
        if (Repo.Pipeline is { Kind: "failed" } p && Repo.Remote is not null && Forges.For(Repo.Remote) is { } client)
        {
            try
            {
                var jobs = await client.ListJobsAsync(Repo.Remote, p.Id);
                Replace(FailedJobs, jobs.Where(j => j.Status == "failed" && !j.AllowFailure));
            }
            catch
            {
                // overview only - the Pipelines tab shows errors
            }
        }
    }
}
