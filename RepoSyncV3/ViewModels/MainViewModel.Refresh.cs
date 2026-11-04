using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class MainViewModel
{
    // =================================================================================== refresh

    public async Task InitializeAsync()
    {
        if (RememberedSelection is { } last)
            SelectedRepo = Repos.FirstOrDefault(r => string.Equals(r.Path, last, StringComparison.OrdinalIgnoreCase));
        if (SelectedRepo is not null && !InScope(SelectedRepo)) SelectedRepo = null;
        if (SelectedRepo is null) await Dashboard.LoadAsync();
        // worktrees, refresh, then a quiet fetch of watched repos so their ahead/behind are true numbers
        await LoadReposAsync(Repos.ToList());
        ApplyAutoTimer();
    }

    /// <summary>Server refresh for watched repos; unwatched ones get local status only (no network).</summary>
    private async Task RefreshWatchedAsync(IReadOnlyCollection<RepoViewModel> repos)
    {
        foreach (var repo in repos.Where(r => !r.Watched && !r.IsBusy).ToList()) await repo.RefreshLocalAsync();
        await RefreshReposAsync(repos.Where(r => r.Watched).ToList(), fetch: false);
        RecountSoon();
    }

    public async Task RefreshRepoAsync(RepoViewModel repo)
    {
        await repo.RefreshLocalAsync();
        await RefreshServerAsync(repo);
        if (Detail?.Repo == repo) await Detail.LoadCurrentTabAsync();
    }

    /// <summary>Refreshes repos in parallel (6 at a time): optionally fetch, then local status, then the server.</summary>
    private async Task RefreshReposAsync(IReadOnlyList<RepoViewModel> repos, bool fetch, bool quiet = false)
    {
        if (repos.Count == 0) return;
        Busy(+1);
        StatusText = fetch ? $"Fetching {repos.Count} repo(s)..." : $"Refreshing {repos.Count} repo(s)...";
        if (!quiet && fetch) AddLog($"Fetch all ({repos.Count} repos)", LogLevel.Header);
        var failed = 0;
        using var gate = new SemaphoreSlim(6);
        var tasks = repos.Select(async repo =>
        {
            await gate.WaitAsync();
            var before = repo.Loaded ? repo.ToSnapshot() : null;
            try
            {
                if (fetch && !repo.IsBusy && Directory.Exists(repo.Path))
                {
                    try
                    {
                        await new RepoActions(repo.Path, new Progress<LogEntry>(_ => { })).FetchAsync(quiet: true);
                    }
                    catch (UserFacingException ex)
                    {
                        Interlocked.Increment(ref failed);
                        if (!quiet) AddLog($"{repo.Name}: fetch failed - {string.Join(" ", ex.Help).Trim()}", LogLevel.Warning);
                    }
                }
                await repo.RefreshLocalAsync();
                if (fetch) await AutoFastForwardAsync(repo);
                await RefreshServerAsync(repo);
                if (before is not null) AddFeed(ChangeFeed.Diff(repo.Name, before, repo.ToSnapshot()));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        _lastLocalRefresh = DateTime.Now;
        SaveStatusCache();
        OverviewView.Refresh();
        Busy(-1);
        StatusText = $"{(fetch ? "Fetched" : "Refreshed")} {repos.Count} repo(s) at {DateTime.Now:HH:mm}" + (failed > 0 ? $" - {failed} fetch failure(s), see log" : string.Empty);
        if (!quiet && fetch) AddLog(StatusText, failed > 0 ? LogLevel.Warning : LogLevel.Success);
        if (Detail is not null) await Detail.LoadCurrentTabAsync();
        else if (!quiet) await Dashboard.LoadAsync();
        RecountSoon();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The repo's "keep up to date" setting: after a fetch, fast-forward only when that can't touch any work
    /// (clean, on a branch, nothing to push, nothing in progress).</summary>
    private async Task AutoFastForwardAsync(RepoViewModel repo)
    {
        if (!repo.Entry.AutoFastForward || repo.IsBusy || repo.Behind == 0 || repo.Ahead > 0 || repo.IsDirty || repo.InProgress
            || repo.Detached || !repo.HasUpstream) return;
        AddLog($"{repo.Name}: git merge --ff-only @{{u}} (keep up to date)", LogLevel.Command);
        var r = await Git.RunAsync(repo.Path, new[] { "merge", "--ff-only", "@{u}" });
        if (r.Success) AddLog($"{repo.Name}: fast-forwarded {repo.Behind} commit(s) from {repo.Upstream}.", LogLevel.Success);
        else AddLog($"{repo.Name}: automatic fast-forward skipped - {r.CombinedOutput.Trim()}", LogLevel.Warning);
        await repo.RefreshLocalAsync();
    }

    /// <summary>Pulls every repo (in the current workspace) that can be fast-forwarded safely (clean, on a branch,
    /// behind, nothing to push). Anything else is skipped and listed, so nothing is ever merged or rebased behind
    /// your back.</summary>
    private async Task PullAllAsync(List<RepoViewModel>? only = null)
    {
        var scope = only ?? ScopeRepos;
        await RefreshReposAsync(scope, fetch: true);
        var behind = scope.Where(r => r.Behind > 0).ToList();
        if (behind.Count == 0)
        {
            ShowBanner("Everything is up to date", $"No repo in {ScopeName} has new commits on the server.", "success");
            return;
        }
        var safe = behind.Where(r => r.Ahead == 0 && !r.IsDirty && !r.InProgress && !r.Detached).ToList();
        var skipped = behind.Except(safe).Select(r => BulkResult.Skipped(r, PullSkipReason(r))).ToList();
        AddLog($"Pull all ({ScopeName}): {safe.Count} to fast-forward, {skipped.Count} skipped", LogLevel.Header);
        await RunBulkAsync("Pull all", safe, skipped, r => RunRepoAsync(r, "Pull", a => a.PullAsync(), refreshServer: false));
        await Dashboard.LoadAsync();
    }

    /// <summary>Pushes every repo (in the current workspace) whose branch is simply ahead of the server: published,
    /// nothing to pull, no merge in progress, and committed with the right identity.</summary>
    /// <summary>Commit all: every repo with changes (in the workspace, or the picked cards), each with its own message,
    /// after you review the list. Repos that can't be committed safely are shown unticked with the reason.</summary>
    private async Task CommitAllAsync(List<RepoViewModel>? only = null)
    {
        var scope = only ?? ScopeRepos.ToList();
        await Task.WhenAll(scope.Where(r => Directory.Exists(r.Path)).Select(r => r.RefreshLocalAsync()));
        var dirty = scope.Where(r => r.Loaded && r.IsDirty).OrderBy(r => r.Name).ToList();
        if (dirty.Count == 0)
        {
            ShowBanner("Nothing to commit", $"No repo in {(only is null ? ScopeName : "the selection")} has changes.", "success");
            return;
        }

        var rows = new List<CommitAllRow>();
        foreach (var r in dirty)
        {
            var status = await Git.GetStatusAsync(r.Path);
            var note = r.IdentityMismatch && Settings.IdentityCheck == "warn" ? $"Commits as {r.GitEmail}, not the {r.WorkspaceName} identity." : null;
            rows.Add(new CommitAllRow(r, CommitAllRow.Suggest(r, new RepoStatusFiles(status.Staged, status.Unstaged)),
                CommitAllRow.Blocked(r, Settings.IdentityCheck), note));
        }
        if (CommitAllWindow.Ask(only is null ? ScopeName : "the selected repos", rows) is not { } choice) return;

        AddLog($"Commit all: {choice.Rows.Count} repo(s){(choice.Push ? " and push" : string.Empty)}", LogLevel.Header);
        var messages = choice.Rows.ToDictionary(r => r.Repo, r => r.Message);
        var notTicked = rows.Where(r => !messages.ContainsKey(r.Repo) && r.BlockedReason is not null)
            .Select(r => BulkResult.Skipped(r.Repo, r.BlockedReason!)).ToList();
        await RunBulkAsync("Commit all", choice.Rows.Select(r => r.Repo).ToList(), notTicked, repo =>
        {
            var push = choice.Push && repo.RemoteName is not null;
            return RunRepoAsync(repo, push ? "Commit & push" : "Commit", async a =>
            {
                // a retry after a commit went through but its push failed: only push
                if ((await Git.GetStatusAsync(repo.Path)) is { Staged.Count: 0, Unstaged.Count: 0 })
                    return push ? await a.PushAsync() : "Nothing left to commit";
                await a.StageAllAsync();
                await a.CommitAsync(messages[repo], amend: false);
                return push ? "Committed; " + await a.PushAsync() : "Committed";
            }, refreshServer: push);
        });
        RecountSoon();
    }

    private async Task PushAllAsync(List<RepoViewModel>? only = null)
    {
        var scope = only ?? ScopeRepos;
        await RefreshReposAsync(scope, fetch: true);
        var ahead = scope.Where(r => r.Ahead > 0).ToList();
        if (ahead.Count == 0)
        {
            ShowBanner("Nothing to push", $"No repo in {ScopeName} has commits waiting to go to the server.", "success");
            return;
        }
        var safe = ahead.Where(r => r.HasUpstream && r.Behind == 0 && !r.InProgress && !r.Detached && !r.IdentityMismatch).ToList();
        var skipped = ahead.Except(safe).ToList();
        var list = string.Join("\n", safe.Select(r => $"  {r.Name}: {r.Ahead} commit(s) to {r.Upstream}"));
        if (safe.Count > 0 && !PromptWindow.Confirm("Push all", $"Push these {safe.Count} repo(s)?\n\n{list}", "Push all")) return;
        AddLog($"Push all ({ScopeName}): {safe.Count} to push, {skipped.Count} skipped", LogLevel.Header);
        await RunBulkAsync("Push all", safe, skipped.Select(r => BulkResult.Skipped(r, PushSkipReason(r))).ToList(),
            r => RunRepoAsync(r, "Push", a => a.PushAsync()));
        await Dashboard.LoadAsync();
    }

    /// <summary>Runs one action per repo in turn, then: everything fine = a banner; anything skipped or failed = the
    /// results window (with Retry for the failed ones, as often as you like).</summary>
    private async Task RunBulkAsync(string title, IReadOnlyList<RepoViewModel> repos, IReadOnlyList<BulkResult> skipped,
        Func<RepoViewModel, Task<bool>> run)
    {
        var todo = repos.ToList();
        var results = new List<BulkResult>();
        while (true)
        {
            foreach (var repo in todo)
            {
                var ok = await run(repo);
                results.RemoveAll(r => r.Repo == repo);
                results.Add(ok ? BulkResult.Done(repo, LastRunMessage.Length > 0 ? LastRunMessage : "Done")
                               : BulkResult.Error(repo, LastRunMessage.Length > 0 ? LastRunMessage : "Failed (see Activity)"));
            }
            var all = results.Concat(skipped).ToList();
            if (all.All(r => r.Tone == "success"))
            {
                ShowBanner($"{title}: done", $"{results.Count} repo(s): all done.", "success");
                return;
            }
            BannerVisible = false;
            if (!BulkResultsWindow.Show($"{title}: results", all, r => SelectedRepo = r)) return;
            todo = results.Where(r => r.Failed).Select(r => r.Repo).ToList();
        }
    }

    private static string PullSkipReason(RepoViewModel r) =>
        r.InProgress ? "A merge or rebase is in progress - finish or abort it first"
        : r.Detached ? "Not on a branch (detached HEAD)"
        : r.IsDirty && r.Ahead > 0 ? "Has uncommitted changes and unpushed commits - pull it on its own"
        : r.IsDirty ? "Has uncommitted changes - pull it on its own (they are kept)"
        : r.Ahead > 0 ? $"Diverged: {r.Ahead} local commit(s) not on the server - pull it on its own to merge or rebase"
        : "Can't be fast-forwarded";

    private static string PushSkipReason(RepoViewModel r) =>
        !r.HasUpstream ? "Branch isn't published yet - push it on its own"
        : r.Behind > 0 ? $"The server has {r.Behind} new commit(s) - pull first"
        : r.InProgress ? "A merge or rebase is in progress"
        : r.Detached ? "Not on a branch (detached HEAD)"
        : r.IdentityMismatch ? $"Commits as {r.GitEmail}, not the {r.WorkspaceName} identity"
        : "Can't be pushed safely";

    /// <summary>Deletes local branches that are merged or gone from the server, in every repo of the workspace,
    /// after you tick which ones.</summary>
    private async Task CleanupAllAsync()
    {
        var candidates = new List<(RepoViewModel Repo, string Branch)>();
        foreach (var repo in ScopeRepos.Where(r => r is { Loaded: true, IsBusy: false, RemoteName: not null } && Directory.Exists(r.Path)))
        {
            var local = await Git.GetLocalBranchesAsync(repo.Path);
            var merged = await Git.GetMergedBranchesAsync(repo.Path, repo.DefaultRef);
            candidates.AddRange(local
                .Where(b => !b.IsCurrent && b.Name != repo.DefaultBranch && (merged.Contains(b.Name) || b.UpstreamGone))
                .Select(b => (repo, b.Name)));
        }
        if (candidates.Count == 0)
        {
            ShowBanner("Nothing to clean up", $"No repo in {ScopeName} has merged or deleted-on-server branches.", "success");
            return;
        }
        var labels = candidates.Select(c => $"{c.Repo.Name}: {c.Branch}").ToList();
        var chosen = ScanResultsWindow.Ask(labels, "Clean up branches",
            "These local branches are merged into their default branch, or were deleted on the server. Delete the ticked ones:", "Delete branches");
        if (chosen is null || chosen.Count == 0) return;
        var picked = candidates.Where((c, i) => chosen.Contains(labels[i])).GroupBy(c => c.Repo);
        var total = 0;
        foreach (var group in picked)
        {
            var branches = group.Select(g => g.Branch).ToList();
            await RunRepoAsync(group.Key, "Clean up branches", async a =>
            {
                var n = await a.CleanupMergedAsync(branches);
                total += n;
                return string.Empty;
            }, refreshServer: false);
        }
        ShowBanner("Clean up finished", $"Deleted {total} branch(es).", "success");
    }

    /// <summary>Something outside the app changed a repo's HEAD, index or refs: re-read that repo (no network).</summary>
    private async void OnRepoChangedOnDisk(string path)
    {
        if (Repos.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) is not { IsBusy: false } repo) return;
        try
        {
            await repo.RefreshLocalAsync();
            if (Detail?.Repo == repo && Detail.SelectedTab is RepoDetailViewModel.TabOverview or RepoDetailViewModel.TabChanges)
                await Detail.LoadCurrentTabAsync();
            if (Detail is null) Dashboard.RebuildLocal();
            RecountSoon();
        }
        catch (Exception ex)
        {
            AddLog($"{repo.Name}: refresh after an outside change failed: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Stops the .git watchers (window closing).</summary>
    public void Shutdown()
    {
        _watcher.Dispose();
        SaveStatusCache();
    }

    /// <summary>Window got focus again: re-read local status (cheap, no network) so it reflects what you just
    /// did in your editor. At most every 5 seconds.</summary>
    public async Task OnActivatedAsync()
    {
        if ((DateTime.Now - _lastLocalRefresh).TotalSeconds < 5 || IsBusy) return;
        _lastLocalRefresh = DateTime.Now;
        foreach (var repo in Repos.Where(r => !r.IsBusy).ToList()) await repo.RefreshLocalAsync();
        if (Detail is not null && Detail.SelectedTab is RepoDetailViewModel.TabOverview or RepoDetailViewModel.TabChanges)
            await Detail.LoadCurrentTabAsync();
        if (Detail is null) Dashboard.RebuildLocal();
        RecountSoon();
    }

    /// <summary>When each loaded profile was last fetched in the background (profile id -> time).</summary>
    private readonly Dictionary<string, DateTime> _lastAutoFetch = new();

    /// <summary>Timer tick (every minute): fetches the watched repos of each loaded profile whose interval is up.</summary>
    private async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        var now = DateTime.Now;
        var due = Repos.Select(r => r.Workspace).Distinct()
            .Where(w => w.AutoFetchMinutes is > 0 and var m
                        && (!_lastAutoFetch.TryGetValue(w.Id, out var last) || (now - last).TotalMinutes >= m - 0.5))
            .Select(w => w.Id).ToHashSet();
        if (due.Count == 0) return;
        foreach (var id in due) _lastAutoFetch[id] = now;
        var watched = Repos.Where(r => r.Watched && due.Contains(r.WorkspaceId)).ToList();
        var behindBefore = watched.ToDictionary(r => r, r => r.Behind);
        await RefreshReposAsync(watched, fetch: true, quiet: true);
        if (!Settings.Notifications) return;
        foreach (var r in watched)
        {
            var added = r.Behind - behindBefore[r];
            if (added > 0 && r.Workspace.Notifications)
                Notify?.Invoke($"[{r.WorkspaceName}] {r.Name}: {added} new commit(s) on the server", $"{r.Branch} is {r.Behind} behind {r.Upstream}. Pull to update.");
        }
    }

    /// <summary>The timer runs while any loaded profile has background fetch on; each profile keeps its own interval.</summary>
    private void ApplyAutoTimer()
    {
        _autoTimer.Stop();
        if (!Repos.Any(r => r.Workspace.AutoFetchMinutes > 0)) return;
        _autoTimer.Interval = TimeSpan.FromMinutes(1);
        _autoTimer.Start();
    }
}
