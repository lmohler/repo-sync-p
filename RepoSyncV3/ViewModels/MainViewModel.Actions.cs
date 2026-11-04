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
    // =================================================================================== running actions

    /// <summary>
    /// Runs one repo action: marks the repo busy, logs a header, shows a green/red banner with the outcome, then
    /// refreshes the repo (and the open tab). <paramref name="action"/> returns the success summary (empty for
    /// no banner).
    /// </summary>
    /// <summary>Shows git's transfer progress ("Receiving objects: 42%") as the repo's busy text, at most ~8 times a second.</summary>
    private static Action<string> ProgressReporter(RepoViewModel repo, string title)
    {
        var last = 0L;
        return line =>
        {
            if (!line.Contains('%')) return;
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref last) < 120) return;
            Interlocked.Exchange(ref last, now);
            var t = line.Trim();
            if (t.StartsWith("remote:", StringComparison.Ordinal)) t = t["remote:".Length..].Trim();
            var text = $"{title}: {t}";
            Application.Current.Dispatcher.BeginInvoke(() => { if (repo.IsBusy) repo.BusyText = text; });
        };
    }

    /// <summary>What the last <see cref="RunRepoAsync"/> ended with: its summary, or the error (for bulk results).</summary>
    private string LastRunMessage { get; set; } = string.Empty;

    public async Task<bool> RunRepoAsync(RepoViewModel repo, string title, Func<RepoActions, Task<string>> action, bool refreshServer = true,
        bool undoable = true)
    {
        if (repo.IsBusy) return false;
        repo.IsBusy = true;
        repo.BusyText = title + "...";
        Busy(+1);
        StatusText = $"{repo.Name}: {title}...";
        AddLog($"{repo.Name} - {title}", LogLevel.Header);
        var ok = false;
        LastRunMessage = string.Empty;
        var ct = repo.BeginWork();
        UndoPoint? before = null, after = null;
        try
        {
            // every action is snapshotted for the Undo history; `undoable` only decides the banner's Undo button
            before = await UndoPoint.TakeAsync(repo.Path, title, ct);
            var summary = await action(new RepoActions(repo.Path, _log, ct)
            {
                PrePushCommand = repo.Entry.PrePushCommand,
                PushAnyway = output => Application.Current.Dispatcher.Invoke(() => PromptWindow.Confirm("Pre-push check failed",
                    $"{repo.Name}: '{repo.Entry.PrePushCommand}' failed.\n\n{output}\n\nPush anyway?", "Push anyway")),
                Progress = ProgressReporter(repo, title),
            });
            after = before is null ? null : await UndoPoint.TakeAsync(repo.Path, title, ct);
            var moved = undoable && before is not null && after is not null && before.DiffersFrom(after);
            repo.LastUndo = moved ? before : repo.LastUndo;
            if (summary.Length > 0 || moved)
            {
                ShowBanner($"{title}: done", summary.Length > 0 ? $"{repo.Name}: {summary}" : repo.Name, "success", moved ? repo : null);
                if (summary.Length > 0) StatusText = $"{repo.Name}: {summary}";
            }
            ok = true;
            LastRunMessage = summary;
        }
        catch (UserFacingException ex)
        {
            LastRunMessage = ex.Message;
            AddLog(ex.Message, LogLevel.Error);
            foreach (var h in ex.Help) AddLog("  " + h, LogLevel.Error);
            ShowBanner(ex.Message, string.Join("\n", ex.Help), "error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastRunMessage = "Cancelled";
            AddLog($"{title} cancelled - git was stopped.", LogLevel.Warning);
            ShowBanner($"{title} cancelled", $"{repo.Name}: git was stopped part-way. Check the Overview tab - if a merge or rebase was left open, Continue or Abort it there.", "warning");
        }
        catch (Exception ex)
        {
            LastRunMessage = ex.Message;
            AddLog(ex.ToString(), LogLevel.Error);
            ShowBanner($"{title} failed", ex.Message, "error");
        }
        finally
        {
            repo.EndWork();
            repo.IsBusy = false;
            repo.BusyText = string.Empty;
            Busy(-1);
        }
        await RecordUndoAsync(repo, title, before, after, ok);

        await repo.RefreshLocalAsync();
        if (refreshServer) await RefreshServerAsync(repo);
        if (Detail?.Repo == repo) await Detail.LoadCurrentTabAsync();
        CommandManager.InvalidateRequerySuggested();
        return ok;
    }

    /// <summary>Same as <see cref="RunRepoAsync"/> for GitHub / GitLab API calls (no git, no repo refresh unless asked).</summary>
    public async Task<T?> RunApiAsync<T>(string title, Func<Task<T>> call) where T : class
    {
        Busy(+1);
        try
        {
            return await call();
        }
        catch (UserFacingException ex)
        {
            AddLog($"{title}: {ex.Message}", LogLevel.Error);
            ShowBanner(ex.Message, string.Join("\n", ex.Help), "error");
        }
        catch (Exception ex)
        {
            AddLog($"{title}: {ex}", LogLevel.Error);
            ShowBanner($"{title} failed", ex.Message, "error");
        }
        finally
        {
            Busy(-1);
        }
        return null;
    }

    /// <summary>For API calls with no result: true when it worked.</summary>
    public async Task<bool> RunApiAsync(string title, Func<Task> call) =>
        await RunApiAsync(title, async () =>
        {
            await call();
            return new object();
        }) is not null;

    private async Task RefreshServerAsync(RepoViewModel repo)
    {
        var notes = await repo.RefreshServerAsync();
        if (Settings.Notifications && repo.Workspace.Notifications)
        {
            foreach (var (t, x) in notes) Notify?.Invoke($"[{repo.WorkspaceName}] {t}", x);
        }
    }

    public async Task RunNextStepAsync(RepoViewModel? repo)
    {
        if (repo is null) return;
        if (repo != SelectedRepo) SelectedRepo = repo;
        switch (repo.Next.Action)
        {
            case "remove": RemoveRepo(repo); break;
            case "refresh": await RefreshRepoAsync(repo); break;
            case "changes": Detail?.GoToTab(RepoDetailViewModel.TabChanges); break;
            case "pipelines": Detail?.GoToTab(RepoDetailViewModel.TabPipelines); break;
            case "mrs": Detail?.GoToTab(RepoDetailViewModel.TabMergeRequests); break;
            case "fix-identity": await FixIdentityAsync(repo); break;
            case "continue": await RunRepoAsync(repo, "Continue", async a => { await a.ContinueAsync(); return "Done"; }); break;
            case "pull": await RunRepoAsync(repo, "Pull", a => a.PullAsync()); break;
            case "push": await PushAsync(repo); break;
            case "sync": await RunRepoAsync(repo, "Pull & push", a => a.SyncAsync()); break;
            case "update": await RunRepoAsync(repo, "Update from default branch", a => a.UpdateFromDefaultAsync()); break;
            case "finish": await FinishBranchAsync(repo); break;
            case "create-mr": await CreateMergeRequestAsync(repo); break;
            case "start-branch": await StartBranchAsync(repo); break;
        }
    }

    // =================================================================================== git identity

    /// <summary>Makes the repo commit as its workspace's identity (repo-level git config).</summary>
    public async Task FixIdentityAsync(RepoViewModel repo)
    {
        var w = repo.Workspace;
        if (w.GitEmail.Trim().Length == 0)
        {
            ShowBanner($"No identity set for {w.Name}", $"Set the {w.Name} name and email in Settings > Profiles first.", "warning");
            return;
        }
        await RunRepoAsync(repo, "Use profile identity", async a =>
        {
            await a.SetLocalIdentityAsync(w.GitName.Trim().Length > 0 ? w.GitName : repo.GitName, w.GitEmail);
            return $"Commits now use {w.GitEmail}";
        }, refreshServer: false);
    }

    /// <summary>
    /// Called before a commit or push. When the repo's author email doesn't match its workspace: "block" asks what
    /// to do (use the workspace identity, move the repo to the other workspace, go ahead anyway, or cancel);
    /// "warn" shows a banner and goes ahead. True = go ahead.
    /// </summary>
    public async Task<bool> CheckIdentityAsync(RepoViewModel repo, string what)
    {
        await repo.RefreshLocalAsync();
        for (var attempt = 0; attempt < 2 && repo.IdentityMismatch; attempt++)
        {
            if (Settings.IdentityCheck == "warn")
            {
                ShowBanner($"Wrong identity for a {repo.WorkspaceName} repo", repo.IdentityProblem, "warning");
                AddLog($"{repo.Name}: {repo.IdentityProblem}", LogLevel.Warning);
                return true;
            }
            switch (IdentityWindow.Ask(repo, what, out var moveTo))
            {
                case IdentityChoice.UseWorkspace:
                    await FixIdentityAsync(repo);
                    break;
                case IdentityChoice.MoveWorkspace:
                    repo.SetWorkspace(moveTo);
                    SaveSettings();
                    FollowRepo(repo);
                    AddLog($"{repo.Name} moved to {repo.WorkspaceName}.", LogLevel.Success);
                    break;
                case IdentityChoice.Proceed:
                    AddLog($"{repo.Name}: {what} as {repo.GitEmail} although the repo is in {repo.WorkspaceName} (confirmed).", LogLevel.Warning);
                    return true;
                default:
                    return false;
            }
        }
        return true;
    }

    private async Task PushAsync(RepoViewModel repo)
    {
        if (!await CheckIdentityAsync(repo, "push")) return;
        await RunRepoAsync(repo, "Push", a => a.PushAsync());
    }

    // =================================================================================== workflow dialogs

    public async Task StartBranchAsync(RepoViewModel repo)
    {
        var user = await RepoActions.GetBranchUserAsync(repo.Path);
        var pattern = Settings.BranchPattern;
        var locals = repo.RemoteName is null ? (await Git.GetLocalBranchesAsync(repo.Path)).Select(b => b.Name).ToList() : null;
        var choice = StartBranchWindow.Ask(repo, ref pattern, user, locals);
        if (choice is null) return;
        Settings.BranchPattern = pattern;
        SaveSettings();
        await RunRepoAsync(repo, "Start branch", async a =>
        {
            await a.StartBranchAsync(choice.Value.Branch, choice.Value.StartPoint, choice.Value.Publish);
            return $"Now on '{choice.Value.Branch}'";
        });
    }

    public async Task CreateMergeRequestAsync(RepoViewModel repo)
    {
        if (repo.Remote is null) return;
        if (!repo.HasToken)
        {
            ShowBanner($"No {repo.ServerName} token for {repo.Remote.Host}", $"Click the key button (Accounts) in the toolbar to add one; {repo.MrTerm}s need it.", "warning");
            return;
        }
        if (repo.HasMergeRequest && repo.MergeRequest!.State == "opened")
        {
            ShowBanner($"'{repo.Branch}' already has {repo.MergeRequest.Reference}", repo.MergeRequest.Title, "info");
            Detail?.GoToTab(RepoDetailViewModel.TabMergeRequests);
            return;
        }

        // the server needs the branch first
        if (!repo.HasUpstream || repo.Ahead > 0)
        {
            if (!await CheckIdentityAsync(repo, "push")) return;
            if (!await RunRepoAsync(repo, "Push", a => a.PushAsync())) return;
        }

        var subjects = repo.RemoteName is null ? new List<CommitInfo>()
            : await Git.GetLogAsync(repo.Path, $"{repo.DefaultRef}..HEAD", 50);
        var targets = new List<string> { repo.DefaultBranch ?? "main" };
        if (repo.RemoteName is { } remoteName)
        {
            targets.AddRange((await Git.GetRemoteBranchesAsync(repo.Path, remoteName))
                .Select(b => b.Name[(remoteName.Length + 1)..])
                .Where(n => n != repo.Branch && !targets.Contains(n)));
        }
        var request = CreateMergeRequestWindow.Ask(repo, subjects.Select(c => c.Subject).Reverse().ToList(), Settings, targets);
        if (request is null || !ServerWriteNotice.Confirm("create", $"Create {repo.MrTerm}")) return;

        var client = Forges.Require(repo.Remote);
        var mr = await RunApiAsync($"Create {repo.MrTerm}", () => client.CreateMergeRequestAsync(
            repo.Remote, repo.Branch, request.Target, request.Title, request.Description, request.Draft, request.RemoveSource, request.Squash));
        if (mr is null) return;

        AddLog($"Created {mr.Reference}: {mr.Title}  {mr.WebUrl}", LogLevel.Success);
        ShowBanner($"Created {repo.MrTerm} {mr.Reference}", mr.Title, "success");
        if (request.OpenInBrowser) Shell.OpenUrl(mr.WebUrl);
        await RefreshServerAsync(repo);
        if (Detail?.Repo == repo) await Detail.LoadCurrentTabAsync();
    }

    public async Task FinishBranchAsync(RepoViewModel repo)
    {
        var merged = repo.MrKind == "merged";
        var text = merged
            ? $"{repo.MergeRequest!.Reference} is merged. Switch to '{repo.DefaultBranch}', update it, and delete '{repo.Branch}' here and on the server?"
            : $"Switch to '{repo.DefaultBranch}', update it, and delete '{repo.Branch}' here and on the server?\n\nThis only goes ahead if the branch's commits are already in {repo.DefaultRef}.";
        if (!PromptWindow.Confirm("Finish branch", text, "Finish branch")) return;
        await RunRepoAsync(repo, "Finish branch", a => a.FinishBranchAsync(merged));
    }
}
