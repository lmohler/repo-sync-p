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
    // =================================================================================== worktrees

    public ObservableCollection<WorktreeInfo> Worktrees { get; } = new();
    private WorktreeInfo? _selectedWorktree;
    private int _linkedWorktreeCount;

    public WorktreeInfo? SelectedWorktree { get => _selectedWorktree; set => Set(ref _selectedWorktree, value); }

    /// <summary>Worktrees other than the main one (for the tab header).</summary>
    public int LinkedWorktreeCount { get => _linkedWorktreeCount; private set { if (Set(ref _linkedWorktreeCount, value)) OnPropertyChanged(nameof(ShowWorktreesTab)); } }

    private async Task LoadWorktreesAsync()
    {
        var list = await Git.GetWorktreesAsync(Repo.Path);
        foreach (var w in list) w.InList = _main.Repos.Any(r => string.Equals(r.Path, w.Path, StringComparison.OrdinalIgnoreCase));
        var keep = SelectedWorktree?.Path;
        Replace(Worktrees, list);
        LinkedWorktreeCount = list.Count(w => !w.IsMain);
        SelectedWorktree = Worktrees.FirstOrDefault(w => string.Equals(w.Path, keep, StringComparison.OrdinalIgnoreCase))
            ?? Worktrees.FirstOrDefault(w => !w.IsCurrent);
    }

    private async Task AddWorktreeAsync(object? _)
    {
        var branch = PromptWindow.Ask("Add worktree",
            $"Branch to check out in the new folder. An existing local branch is used as-is; a new name is created from {Repo.DefaultRef}.",
            string.Empty, "Next");
        branch = branch?.Trim();
        if (string.IsNullOrEmpty(branch)) return;

        // default next to the main repo, like C:\code\.web-portal-work for web-portal on 'work'
        var main = Worktrees.FirstOrDefault(w => w.IsMain)?.Path ?? Repo.WorktreeOf ?? Repo.Path;
        var safe = string.Concat(branch.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '-' : c));
        var suggested = Path.Combine(Path.GetDirectoryName(main) ?? main, $".{Path.GetFileName(main)}-{safe}");
        var folder = PromptWindow.Ask("Add worktree", $"Folder for '{branch}' (created by git, must not exist yet):", suggested, "Create");
        folder = folder?.Trim().Trim('"');
        if (string.IsNullOrEmpty(folder)) return;
        folder = Path.GetFullPath(folder);

        var ok = await _main.RunRepoAsync(Repo, $"Add worktree {branch}", async a =>
        {
            await a.AddWorktreeAsync(folder, branch, Repo.DefaultRef);
            return $"'{branch}' is checked out in {folder}";
        }, refreshServer: false);
        if (ok) await _main.OpenWorktreeAsync(folder, select: false);
        await LoadWorktreesAsync();
    }

    private async Task RemoveWorktreeAsync(object? _)
    {
        var w = SelectedWorktree!;
        var force = false;
        if (!w.Prunable && Directory.Exists(w.Path))
        {
            var status = await Git.GetStatusAsync(w.Path);
            var changes = status.Staged.Count + status.Unstaged.Count + status.Conflicted.Count;
            if (changes > 0)
            {
                if (!PromptWindow.Confirm("Remove worktree",
                        $"{w.Path} has {changes} uncommitted change(s). Removing it deletes them for good.\n\nThe branch '{w.BranchText}' and its commits are kept.",
                        "Delete anyway", danger: true)) return;
                force = true;
            }
        }
        if (!force && !PromptWindow.Confirm("Remove worktree",
                $"Delete the folder {w.Path}?\n\nThe branch '{w.BranchText}' and its commits are kept.", "Remove", danger: true)) return;

        var ok = await _main.RunRepoAsync(Repo, "Remove worktree", async a =>
        {
            await a.RemoveWorktreeAsync(w.Path, force);
            return $"Removed worktree {w.Path}";
        }, refreshServer: false);
        if (ok) _main.ForgetRepoPath(w.Path);
    }
}
