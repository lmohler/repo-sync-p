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
    // =================================================================================== repo list

    private async Task AddRepoAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a git repository folder", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        if (AskTargetProfile($"Add {dialog.FolderNames.Length} folder(s)") is not { } profile) return;
        var added = new List<RepoViewModel>();
        foreach (var folder in dialog.FolderNames)
        {
            if (!Git.IsRepo(folder))
            {
                ShowBanner("Not a git repository", $"{folder} has no .git folder. Use Clone to get a project from GitHub or GitLab.", "warning");
                continue;
            }
            if (AddRepoPath(folder, profile) is { } r) added.Add(r);
        }
        var first = added.FirstOrDefault();
        added.AddRange(await AddLinkedWorktreesAsync(added));
        await RefreshReposAsync(added, fetch: false);
        if (first is not null) SelectedRepo = first;
    }

    /// <summary>Add SVN repo: checkout (or existing working copy) + git mirror on main + work branch in its own folder,
    /// then both folders go in the list. Setup skips steps already done, so it also finishes a half-set-up folder.</summary>
    private async Task AddSvnRepoAsync()
    {
        var root = Workspaces.Get(TargetWorkspace).CloneRoot;
        var parent = Directory.Exists(root) ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code");
        if (AddSvnRepoWindow.Ask(parent, TargetWorkspace) is not { } request) return;

        Busy(+1);
        StatusText = $"Setting up {request.WorkingCopyPath}...";
        AddLog($"Add SVN repo - {request.SvnUrl ?? request.WorkingCopyPath}", LogLevel.Header);
        try
        {
            var flow = new SvnWorkflow(request.WorkingCopyPath, request.WorkBranch, _log);
            var worktree = await flow.SetupAsync(request.SvnUrl, request.WorktreePath);

            var added = new List<RepoViewModel>();
            foreach (var path in new[] { request.WorkingCopyPath, worktree })
                if (AddRepoPath(path, request.Workspace) is { } r) added.Add(r);
            SaveSettings();
            if (added.LastOrDefault() is { } last) FollowRepo(last);
            await RefreshReposAsync(added, fetch: false);
            SelectedRepo = added.LastOrDefault();
            ShowBanner("SVN repo ready", $"Work on '{request.WorkBranch}' in {worktree}. When it's committed, use the SVN tab to Send it to SVN.", "success");
        }
        catch (UserFacingException ex)
        {
            AddLog(ex.Message, LogLevel.Error);
            foreach (var h in ex.Help) AddLog("  " + h, LogLevel.Error);
            ShowBanner(ex.Message, string.Join("\n", ex.Help), "error");
        }
        catch (Exception ex)
        {
            AddLog(ex.ToString(), LogLevel.Error);
            ShowBanner("Add SVN repo failed", ex.Message, "error");
        }
        finally
        {
            Busy(-1);
            StatusText = "Ready";
        }
    }

    private async Task ScanFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Scan a folder for git repositories" };
        if (dialog.ShowDialog() != true) return;
        StatusText = "Scanning...";
        var found = await Task.Run(() => Git.FindRepos(dialog.FolderName));
        var candidates = found.Where(f => FindEntry(f) is null).ToList();
        if (candidates.Count == 0)
        {
            ShowBanner("No new repositories found", $"Every git repo under {dialog.FolderName} is already in a profile.", "info");
            StatusText = "Ready";
            return;
        }
        var chosen = ScanResultsWindow.Ask(candidates, repoNames: true);
        if (chosen is null || chosen.Count == 0) return;
        if (AskTargetProfile($"Add {chosen.Count} repo(s)") is not { } profile) return;
        var added = chosen.Select(f => AddRepoPath(f, profile)).OfType<RepoViewModel>().ToList();
        AddLog($"Added {added.Count} repo(s) from {dialog.FolderName} to {Workspaces.Get(profile).Name}.", LogLevel.Success);
        added.AddRange(await AddLinkedWorktreesAsync(added));
        await RefreshReposAsync(added, fetch: false);
    }

    /// <summary>The profile new repos go into: the loaded one, or (with "All profiles" and more than one profile)
    /// the one picked in a short prompt. Null = cancelled.</summary>
    private string? AskTargetProfile(string what)
    {
        if (ActiveWorkspace != Workspaces.AllId || Workspaces.All.Count == 1) return TargetWorkspace;
        return PromptWindow.PickProfile("Which profile?", $"{what} to this profile:", TargetWorkspace, "Add");
    }

    /// <summary>The settings entry for a folder in any profile (loaded or not).</summary>
    private RepoEntry? FindEntry(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        return Settings.Repos.FirstOrDefault(e => string.Equals(e.Path, folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a folder to a profile (default: the loaded one) and, if that profile is loaded, to the list.
    /// A folder already in some profile stays where it is; its card is returned.</summary>
    public RepoViewModel? AddRepoPath(string folder, string? profile = null)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        if (FindEntry(folder) is { } known)
        {
            var existing = VmFor(known);
            if (!InScope(existing)) AddLog($"{existing.Name} is already in the {existing.WorkspaceName} profile.", LogLevel.Warning);
            return existing;
        }
        var entry = new RepoEntry { Path = folder, Workspace = Workspaces.Exists(profile) ? profile : TargetWorkspace };
        Settings.Repos.Add(entry);
        var repo = VmFor(entry);
        if (InScope(repo)) Repos.Add(repo);
        SaveSettings();
        RebuildProfileChoices();
        ApplyAutoTimer();
        RecountSoon();
        return repo;
    }

    /// <summary>Adds the linked worktrees of <paramref name="repos"/> that aren't cards yet (so a worktree made in a
    /// terminal shows up on its own). Returns the new cards; prunable (deleted) worktrees are skipped.</summary>
    private async Task<List<RepoViewModel>> AddLinkedWorktreesAsync(IEnumerable<RepoViewModel> repos)
    {
        var added = new List<RepoViewModel>();
        // one main repo per family is enough - every worktree lists all of them; a worktree joins its main repo's profile
        var mains = repos.GroupBy(r => r.WorktreeOf ?? r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Profile: g.First().WorkspaceId)).Where(m => Directory.Exists(m.Path)).ToList();
        foreach (var (main, profile) in mains)
        {
            foreach (var w in await Git.GetWorktreesAsync(main))
            {
                if (w.IsMain || w.Bare || w.Prunable || !Directory.Exists(w.Path)) continue;
                if (FindEntry(w.Path) is not null) continue;
                if (AddRepoPath(w.Path, profile) is { } r)
                {
                    added.Add(r);
                    AddLog($"Added worktree {w.Path} ({w.BranchText}) of {Path.GetFileName(main)}.", LogLevel.Success);
                }
            }
        }
        return added;
    }

    /// <summary>Makes sure a worktree folder is a card, then (optionally) selects it.</summary>
    public async Task OpenWorktreeAsync(string folder, bool select = true)
    {
        var known = FindEntry(folder) is not null;
        var main = Git.MainWorktreePath(folder);
        var family = main is null ? null : FindEntry(main);
        if (AddRepoPath(folder, family?.Workspace) is not { } repo) return;
        if (!known) await RefreshReposAsync(new[] { repo }, fetch: false);
        if (select) SelectedRepo = repo;
    }

    /// <summary>Drops a card without asking (its folder is already gone, e.g. a removed worktree).</summary>
    public void ForgetRepoPath(string folder)
    {
        if (FindEntry(folder) is not { } entry) return;
        var repo = VmFor(entry);
        Settings.Repos.Remove(entry);
        Repos.Remove(repo);
        ForgetVm(repo);
        if (SelectedRepo == repo) SelectedRepo = null;
        SaveSettings();
        RebuildProfileChoices();
        RecountSoon();
    }

    private void RemoveRepo(RepoViewModel? repo)
    {
        if (repo is null) return;
        if (!PromptWindow.Confirm("Remove from list", $"Remove '{repo.Name}' from the list?\n\nThe folder on disk is not touched.", "Remove")) return;
        Settings.Repos.Remove(repo.Entry);
        Repos.Remove(repo);
        ForgetVm(repo);
        if (SelectedRepo == repo) SelectedRepo = null;
        SaveSettings();
        RebuildProfileChoices();
        RecountSoon();
    }

    /// <summary>A folder in the list was moved (e.g. SVN tab > Move work folder): the card follows it, keeping its
    /// name, pin, workspace and settings.</summary>
    public void MoveRepoPath(string oldPath, string newPath)
    {
        if (Repos.FirstOrDefault(r => string.Equals(r.Path, oldPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is not { } old) return;
        var e = old.Entry;
        var entry = new RepoEntry
        {
            Path = Path.GetFullPath(newPath).TrimEnd('\\'),
            DisplayName = e.DisplayName,
            Pinned = e.Pinned,
            Watched = e.Watched,
            Workspace = e.Workspace,
            PrePushCommand = e.PrePushCommand,
            AutoFastForward = e.AutoFastForward,
        };
        var index = Settings.Repos.IndexOf(e);
        if (index >= 0) Settings.Repos[index] = entry;
        else Settings.Repos.Add(entry);
        var wasSelected = SelectedRepo == old;
        Repos.Remove(old);
        ForgetVm(old);
        var repo = VmFor(entry);
        if (InScope(repo)) Repos.Add(repo);
        SaveSettings();
        RecountSoon();
        _ = repo.RefreshLocalAsync();
        if (wasSelected) SelectedRepo = repo;
    }

    private void RenameRepo(RepoViewModel? repo)
    {
        if (repo is null) return;
        var name = PromptWindow.Ask("Display name", "Name shown in the list (leave empty for the folder name):", repo.Entry.DisplayName ?? repo.Name, "Save");
        if (name is null) return;
        repo.RenameDisplay(name);
        ReposView.Refresh();
        SaveSettings();
    }

    /// <summary>Name, workspace and git identity of one repo.</summary>
    private async Task EditRepoAsync(RepoViewModel? repo)
    {
        if (repo is null) return;
        var result = RepoSettingsWindow.Edit(repo);
        if (result is null) return;

        repo.RenameDisplay(result.DisplayName);
        if (result.Workspace != repo.Entry.Workspace) repo.SetWorkspace(result.Workspace);
        repo.Entry.PrePushCommand = result.PrePushCommand;
        repo.Entry.AutoFastForward = result.AutoFastForward;
        repo.SetColor(result.Color);
        SaveSettings();
        FollowRepo(repo);

        switch (result.Identity)
        {
            case RepoIdentityChange.UseWorkspace:
                await FixIdentityAsync(repo);
                break;
            case RepoIdentityChange.UseGlobal:
                await RunRepoAsync(repo, "Use global identity", async a =>
                {
                    await a.ClearLocalIdentityAsync();
                    return "Uses your global git identity";
                }, refreshServer: false);
                break;
            case RepoIdentityChange.Custom:
                await RunRepoAsync(repo, "Set identity", async a =>
                {
                    await a.SetLocalIdentityAsync(result.GitName, result.GitEmail);
                    return $"Commits now use {result.GitEmail}";
                }, refreshServer: false);
                break;
            default:
                await repo.RefreshLocalAsync();
                break;
        }
    }

    private void TogglePin(RepoViewModel? repo)
    {
        if (repo is null) return;
        repo.Pinned = !repo.Pinned;
        ReposView.Refresh();
        RecountSoon();
        SaveSettings();
    }

    private void ToggleWatch(RepoViewModel? repo)
    {
        if (repo is null) return;
        repo.Watched = !repo.Watched;
        StatusText = $"{repo.Name}: background fetch {(repo.Watched ? "on" : "off")}";
        SaveSettings();
    }

    private async Task CloneAsync()
    {
        var choice = CloneWindow.Ask(Settings, TargetWorkspace);
        if (choice is null) return;
        SaveSettings();
        Busy(+1);
        AddLog($"Clone {choice.Url}", LogLevel.Header);
        try
        {
            await RepoActions.CloneAsync(choice.Url, choice.Target, _log, CancellationToken.None);
            var repo = AddRepoPath(choice.Target, choice.Workspace);
            if (repo is not null)
            {
                FollowRepo(repo);
                await repo.RefreshLocalAsync();
                // a fresh clone starts with the workspace's identity, so its first commit is already right
                var w = repo.Workspace;
                if (w.GitEmail.Trim().Length > 0 && repo.IdentityMismatch)
                    await new RepoActions(repo.Path, _log).SetLocalIdentityAsync(w.GitName.Trim().Length > 0 ? w.GitName : repo.GitName, w.GitEmail);
                await repo.RefreshLocalAsync();
                await RefreshServerAsync(repo);
                SelectedRepo = repo;
            }
            ShowBanner("Cloned", choice.Target, "success");
        }
        catch (UserFacingException ex)
        {
            AddLog(ex.Message, LogLevel.Error);
            foreach (var h in ex.Help) AddLog("  " + h, LogLevel.Error);
            ShowBanner(ex.Message, string.Join("\n", ex.Help), "error");
        }
        finally
        {
            Busy(-1);
        }
    }
}
