using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

/// <summary>Everything shown in the tabs for the selected repo. Each tab loads when it is opened.</summary>
public sealed partial class RepoDetailViewModel : ObservableObject
{
    public const int TabOverview = 0;
    public const int TabChanges = 1;
    public const int TabBranches = 2;
    public const int TabHistory = 3;
    public const int TabMergeRequests = 4;
    public const int TabPipelines = 5;
    public const int TabStashes = 6;
    public const int TabTags = 7;
    public const int TabWorktrees = 8;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _pipelinePoll = new() { Interval = TimeSpan.FromSeconds(15) };

    public RepoViewModel Repo { get; }

    public RepoDetailViewModel(MainViewModel main, RepoViewModel repo, int tab)
    {
        _main = main;
        Repo = repo;
        _selectedTab = tab;
        _isSplitDiff = main.Settings.SplitDiff;
        _isFileTree = main.Settings.FileTree;
        Repo.PropertyChanged += Repo_PropertyChanged;

        GoToTabCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var t)) SelectedTab = t;
        });

        // ---- changes
        StageCommand = Cmd(async p => { if (ChangesOf(p) is { Count: > 0 } fs) await Run("Stage", a => a.StageAsync(fs.Select(f => f.Path).ToList())); });
        UnstageCommand = Cmd(async p => { if (ChangesOf(p) is { Count: > 0 } fs) await Run("Unstage", a => a.UnstageAsync(fs.SelectMany(f => new[] { f.OrigPath ?? f.Path, f.Path }).Distinct().ToList())); });
        StageAllCommand = Cmd(_ => Run("Stage all", a => a.StageAllAsync()), _ => Unstaged.Count > 0);
        UnstageAllCommand = Cmd(_ => Run("Unstage all", a => a.UnstageAllAsync()), _ => Staged.Count > 0);
        DiscardCommand = Cmd(async p => { if (ChangesOf(p) is { Count: > 0 } fs) await DiscardAsync(fs); });
        DiscardAllCommand = Cmd(_ => DiscardAsync(Unstaged.ToList()), _ => Unstaged.Count > 0);
        CommitCommand = Cmd(_ => CommitAsync(push: false), _ => CanCommit);
        CommitPushCommand = Cmd(_ => CommitAsync(push: true), _ => CanCommit);
        SuggestMessageCommand = new RelayCommand(SuggestMessage);
        UseTemplateCommand = new RelayCommand(() => CommitMessage = _templateText ?? CommitMessage, () => HasTemplate);
        UndoLastCommitCommand = Cmd(async _ =>
        {
            if (PromptWindow.Confirm("Undo last commit", $"Take back \"{Repo.HeadSubject}\"?\n\nIts changes stay staged so you can fix and commit again.", "Undo commit"))
                await Run("Undo last commit", a => a.UndoLastCommitAsync());
        });
        RecoverCommand = Cmd(async _ =>
        {
            var w = new ReflogWindow(await Git.GetReflogAsync(Repo.Path));
            if (w.ShowDialog() != true) return;
            if (w.Action == "history")
            {
                _main.UndoHistoryCommand.Execute(Repo);
                return;
            }
            if (w.Picked is not { } e) return;
            if (w.Action == "branch")
            {
                var name = PromptWindow.Ask("Branch from here", $"New branch at {e.ShortHash}:", "recovered");
                if (!string.IsNullOrWhiteSpace(name)) await Run("Create branch", a => a.CreateBranchAtAsync(name.Trim(), e.Hash));
                return;
            }
            if (PromptWindow.Confirm("Go back", $"Move '{Repo.Branch}' back to {e.ShortHash} ({e.Action})?\n\nUncommitted changes are kept; commits after that point stay in the reflog, so this can be undone the same way.", "Go back"))
                await Run("Go back", a => a.ResetAsync(e.Hash, "keep"));
        });
        HealthCommand = Cmd(async _ =>
        {
            var w = new HealthWindow(Repo.Name, await RepoHealth.CheckAsync(Repo.Path, Repo.DefaultRef));
            if (w.ShowDialog() != true) return;
            if (w.Action == "cleanup") await CleanupBranchesAsync(null);
            else if (w.Action == "gc") await Run("Compact", a => a.RunGitAsync(new[] { "gc", "--prune=2.weeks.ago" }), "Compacted");
        });
        InteractiveRebaseCommand = Cmd(_ => InteractiveRebaseAsync(), _ => !Repo.IsBusy && !Repo.InProgress && !Repo.Detached);
        RemotesCommand = new RelayCommand(_ =>
            new RemotesWindow(Repo.Path, (title, args, network) => Run(title, a => a.RunGitAsync(args, network))).ShowDialog());
        KeepMineCommand = Cmd(async p => { if (p is FileChange f) await Run("Keep mine", a => a.ResolveAsync(f.Path, keepMine: true)); });
        TakeTheirsCommand = Cmd(async p => { if (p is FileChange f) await Run("Take theirs", a => a.ResolveAsync(f.Path, keepMine: false)); });
        MarkResolvedCommand = Cmd(async p => { if (p is FileChange f) await Run("Mark resolved", a => a.MarkResolvedAsync(f.Path)); });
        ResolveConflictCommand = Cmd(async p =>
        {
            if (p is not FileChange f) return;
            var full = Path.Combine(Repo.Path, f.Path);
            if (!File.Exists(full))
            {
                _main.ShowBanner($"{f.Path} was deleted on one side", "Use Keep mine or Take theirs for this file.", "warning");
                return;
            }
            var (text, bom) = ConflictFile.Read(full);
            var parsed = ConflictFile.Parse(text, Git.IsRebaseInProgress(Repo.Path));
            if (parsed.Blocks.Count == 0)
            {
                _main.ShowBanner($"No conflict markers in {f.Path}", "It's binary or already edited. Use Keep mine / Take theirs, or Mark resolved.", "info");
                return;
            }
            if (ConflictWindow.Resolve(f.Path, parsed) is not { } result) return;
            ConflictFile.Write(full, result, bom);
            await Run("Mark resolved", a => a.MarkResolvedAsync(f.Path));
        });
        ContinueCommand = Cmd(_ => Run("Continue", a => a.ContinueAsync(), "Done"));
        AbortCommand = Cmd(async _ =>
        {
            if (PromptWindow.Confirm("Abort", $"Abort the {(Repo.RebaseInProgress ? "rebase" : "merge")} and put the branch back the way it was?", "Abort", danger: true))
                await Run("Abort", a => a.AbortAsync(), "Aborted");
        });
        OpenFileCommand = new RelayCommand(p =>
        {
            if (p is FileChange f) Shell.OpenInEditor(_main.Settings.EditorCommand, Path.Combine(Repo.Path, f.Path.Replace('/', '\\')), Repo.Path);
            else if (p is CommitFile c) Shell.OpenInEditor(_main.Settings.EditorCommand, Path.Combine(Repo.Path, c.Path.Replace('/', '\\')), Repo.Path);
        });
        ShowInExplorerCommand = new RelayCommand(p =>
        {
            if (p is FileChange f) Shell.SelectInExplorer(Path.Combine(Repo.Path, f.Path.Replace('/', '\\')));
            else if (p is FileTreeFolder d) Shell.SelectInExplorer(Path.Combine(Repo.Path, d.Path.Replace('/', '\\')));
        });

        ForcePushCommand = Cmd(async _ =>
        {
            var preview = await RewritePreview.ForForcePushAsync(Repo.Path, Repo.RemoteName ?? "origin", Repo.Branch);
            if (PreviewWindow.Confirm(preview, "Force push"))
                await Run("Force push", a => a.ForcePushAsync(), "Force pushed");
        });

        MergeBranchCommand = Cmd(async p =>
        {
            var branch = (p as BranchInfo)?.Name ?? (p as string) ?? SelectedLocalBranch?.Name ?? SelectedRemoteBranch?.Name;
            if (string.IsNullOrWhiteSpace(branch)) return;
            if (PromptWindow.Confirm("Merge branch", $"Merge '{branch}' into current branch '{Repo.Branch}'?", "Merge"))
                await Run($"Merge {branch}", a => a.MergeBranchAsync(branch), $"Merged '{branch}'");
        });

        RebaseOntoCommand = Cmd(async p =>
        {
            var branch = (p as BranchInfo)?.Name ?? (p as string) ?? SelectedLocalBranch?.Name ?? SelectedRemoteBranch?.Name;
            if (string.IsNullOrWhiteSpace(branch)) return;
            var preview = await RewritePreview.ForRebaseAsync(Repo.Path, branch);
            if (preview.NothingToDo)
            {
                _main.ShowBanner("Nothing to rebase", $"'{Repo.Branch}' already has everything in '{branch}'.", "info");
                return;
            }
            if (PreviewWindow.Confirm(preview, "Rebase"))
                await Run($"Rebase onto {branch}", a => a.RebaseOntoAsync(branch), $"Rebased onto '{branch}'");
        });

        ResetCommand = Cmd(async p =>
        {
            if (SelectedCommit is not { } c) return;
            var mode = p?.ToString() ?? "mixed";
            var preview = await RewritePreview.ForResetAsync(Repo.Path, c.Hash, mode);
            if (PreviewWindow.Confirm(preview, $"Reset ({mode})", preview.NothingToDo ? "The branch is already at this commit - nothing would change." : null))
                await Run($"Reset ({mode})", a => a.ResetAsync(c.Hash, mode), $"Reset to {c.ShortHash}");
        }, _ => SelectedCommit is not null);

        CreateTagCommand = Cmd(async _ =>
        {
            // the History tab tags its selected commit; from the Tags tab a new tag goes on HEAD
            var w = new CreateTagWindow(Repo.Name, SelectedTab == TabHistory ? SelectedCommit?.ShortHash : null);
            if (w.ShowDialog() == true)
            {
                await Run($"Create tag {w.TagName}", async a =>
                {
                    await a.CreateTagAsync(w.TagName, w.TagMessage, w.TargetCommit == "HEAD" ? null : w.TargetCommit);
                    if (w.PushImmediately && Repo.RemoteName is not null)
                        await a.PushTagAsync(w.TagName);
                }, $"Tag '{w.TagName}' created");
                await LoadTagsAsync();
            }
        });

        DeleteTagCommand = Cmd(async p =>
        {
            var tag = (p as TagInfo)?.Name ?? SelectedTag?.Name;
            if (tag is null) return;
            if (PromptWindow.Confirm("Delete tag", $"Delete local tag '{tag}'?", "Delete", danger: true))
            {
                await Run($"Delete tag {tag}", a => a.DeleteTagAsync(tag), $"Deleted tag '{tag}'");
                await LoadTagsAsync();
            }
        }, _ => SelectedTag is not null);

        PushTagCommand = Cmd(async p =>
        {
            var tag = (p as TagInfo)?.Name ?? SelectedTag?.Name;
            if (tag is null) return;
            await Run($"Push tag {tag}", a => a.PushTagAsync(tag), $"Pushed tag '{tag}'");
        }, _ => SelectedTag is not null && Repo.RemoteName is not null);

        PushAllTagsCommand = Cmd(async _ =>
        {
            if (PromptWindow.Confirm("Push all tags", $"Push all local tags to {Repo.RemoteName}?", "Push tags"))
                await Run("Push all tags", a => a.PushAllTagsAsync(), "All tags pushed");
        }, _ => Repo.RemoteName is not null);

        DeleteRemoteTagCommand = Cmd(async p =>
        {
            var tag = (p as TagInfo)?.Name ?? SelectedTag?.Name;
            if (tag is null) return;
            if (PromptWindow.Confirm("Delete tag on server", $"Delete tag '{tag}' on {Repo.RemoteName}?", "Delete on server", danger: true))
            {
                await Run($"Delete server tag {tag}", a => a.DeleteRemoteTagAsync(tag), $"Deleted tag '{tag}' on {Repo.RemoteName}");
                await LoadTagsAsync();
            }
        }, _ => SelectedTag is not null && Repo.RemoteName is not null);

        AddToGitIgnoreCommand = Cmd(async p =>
        {
            if (p is not string rule || string.IsNullOrWhiteSpace(rule)) return;
            await RepoActions.AddToGitIgnoreAsync(Repo.Path, rule);
            _main.AddLog(new LogEntry($"Added '{rule}' to .gitignore", LogLevel.Success));
            await Repo.RefreshLocalAsync();
            await LoadChangesAsync();
        });

        IgnoreFileCommand = Cmd(async p =>
        {
            var path = (p as FileChange)?.Path ?? (p as string);
            if (!string.IsNullOrWhiteSpace(path))
            {
                await RepoActions.AddToGitIgnoreAsync(Repo.Path, path);
                _main.AddLog(new LogEntry($"Added '{path}' to .gitignore", LogLevel.Success));
                await Repo.RefreshLocalAsync();
                await LoadChangesAsync();
            }
        });

        IgnoreExtCommand = Cmd(async p =>
        {
            var path = (p as FileChange)?.Path ?? (p as string);
            if (!string.IsNullOrWhiteSpace(path) && Path.GetExtension(path) is { Length: > 1 } ext)
            {
                var rule = $"*{ext}";
                await RepoActions.AddToGitIgnoreAsync(Repo.Path, rule);
                _main.AddLog(new LogEntry($"Added '{rule}' to .gitignore", LogLevel.Success));
                await Repo.RefreshLocalAsync();
                await LoadChangesAsync();
            }
        });

        IgnoreFolderCommand = Cmd(async p =>
        {
            var folder = (p as FileTreeFolder)?.Path ?? (p as FileChange)?.Folder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var rule = $"/{folder.TrimStart('/')}/";
                await RepoActions.AddToGitIgnoreAsync(Repo.Path, rule);
                _main.AddLog(new LogEntry($"Added '{rule}' to .gitignore", LogLevel.Success));
                await Repo.RefreshLocalAsync();
                await LoadChangesAsync();
            }
        });

        ViewFileHistoryCommand = Cmd(async p =>
        {
            if (p is FileTreeFolder) return;
            var path = (p as FileChange)?.Path ?? (p as CommitFile)?.Path ?? SelectedChange?.Path ?? SelectedCommitFile?.Path;
            if (string.IsNullOrWhiteSpace(path)) return;
            var commits = await Git.GetFileLogAsync(Repo.Path, path);
            new FileHistoryWindow(Repo.Path, path, commits).ShowDialog();
        });

        PopOutChangeDiffCommand = new RelayCommand(() =>
        {
            if (SelectedChange is not { } f) return;
            new DiffWindow(f.Path, $"{Repo.Name} - working tree{(f.Staged ? " (staged)" : "")}", ChangeDiff, IsSplitDiff).Show();
        }, () => SelectedChange is not null && ChangeDiff.Count > 0);

        PopOutCommitDiffCommand = new RelayCommand(() =>
        {
            if (SelectedCommitFile is not { } f || SelectedCommit is not { } c) return;
            new DiffWindow(f.Path, $"{Repo.Name} - {c.ShortHash} {c.Subject}", CommitDiff, IsSplitDiff).Show();
        }, () => SelectedCommitFile is not null && CommitDiff.Count > 0);

        BlameCommand = Cmd(async p =>
        {
            if (p is FileTreeFolder) return;
            var path = (p as FileChange)?.Path ?? (p as CommitFile)?.Path ?? SelectedChange?.Path ?? SelectedCommitFile?.Path;
            if (string.IsNullOrWhiteSpace(path)) return;
            var lines = await Git.GetBlameAsync(Repo.Path, path);
            new BlameWindow(path, lines).ShowDialog();
        });

        // ---- branches
        SwitchBranchCommand = Cmd(async p =>
        {
            var name = (p as BranchInfo)?.Name ?? SelectedLocalBranch?.Name;
            if (name is not null) await Run($"Switch to {name}", a => a.SwitchAsync(name), $"On '{name}'");
        });
        CheckoutRemoteCommand = Cmd(async p =>
        {
            var name = (p as BranchInfo)?.Name ?? SelectedRemoteBranch?.Name;
            if (name is not null) await Run($"Check out {name}", a => a.SwitchAsync(name), $"Checked out '{name}'");
        });
        DeleteBranchCommand = Cmd(DeleteBranchAsync, _ => SelectedLocalBranch is { IsCurrent: false });
        DeleteRemoteBranchCommand = Cmd(DeleteRemoteBranchAsync, _ => SelectedRemoteBranch is not null);
        RenameBranchCommand = Cmd(RenameBranchAsync, _ => SelectedLocalBranch is not null);
        CleanupBranchesCommand = Cmd(CleanupBranchesAsync);
        OpenBranchCommand = new RelayCommand(p =>
        {
            var name = (p as BranchInfo)?.Name ?? SelectedLocalBranch?.Name;
            if (Repo.Remote is null || name is null) return;
            if (Repo.RemoteName is { } rn && name.StartsWith(rn + "/", StringComparison.Ordinal)) name = name[(rn.Length + 1)..];
            Shell.OpenUrl(ForgeLinks.Branch(Repo.Remote, name));
        }, _ => Repo.Remote is not null);
        CompareCommand = new RelayCommand(() =>
        {
            if (Repo.Remote is not null && Repo.DefaultBranch is not null) Shell.OpenUrl(ForgeLinks.Compare(Repo.Remote, Repo.DefaultBranch, Repo.Branch));
        }, () => Repo.Remote is not null && Repo.IsFeatureBranch);

        // ---- history
        LoadMoreHistoryCommand = Cmd(async _ =>
        {
            _historyMax += 300;
            await LoadHistoryAsync();
        });
        CopyHashCommand = new RelayCommand(() => { if (SelectedCommit is { } c) Clipboard.SetText(c.Hash); }, () => SelectedCommit is not null);
        OpenCommitCommand = new RelayCommand(() => Shell.OpenUrl(ForgeLinks.Commit(Repo.Remote!, SelectedCommit!.Hash)),
            () => Repo.Remote is not null && SelectedCommit is { IsUnpushed: false });
        RevertCommand = Cmd(async _ =>
        {
            var c = SelectedCommit!;
            if (PromptWindow.Confirm("Revert commit", $"Add a new commit that undoes {c.ShortHash} \"{c.Subject}\"?", "Revert"))
                await Run("Revert", a => a.RevertAsync(c.Hash, c.IsMerge), "Revert commit added");
        }, _ => SelectedCommit is not null);
        BranchFromCommitCommand = Cmd(async _ =>
        {
            var c = SelectedCommit!;
            var name = PromptWindow.Ask("Create branch here", $"New branch at {c.ShortHash} \"{c.Subject}\":", string.Empty, "Create");
            if (!string.IsNullOrWhiteSpace(name)) await Run("Create branch", a => a.CreateBranchAtAsync(name.Trim(), c.Hash), $"Created '{name.Trim()}'");
        }, _ => SelectedCommit is not null);
        CherryPickCommand = Cmd(async _ =>
        {
            var c = SelectedCommit!;
            if (PromptWindow.Confirm("Cherry-pick", $"Copy {c.ShortHash} \"{c.Subject}\" onto '{Repo.Branch}' as a new commit?", "Cherry-pick"))
                await Run("Cherry-pick", a => a.CherryPickAsync(c.Hash), "Cherry-picked");
        }, _ => SelectedCommit is not null && HistoryScope != "current");

        // ---- merge requests
        RefreshMergeRequestsCommand = Cmd(_ => LoadMergeRequestsAsync());
        OpenMergeRequestCommand = new RelayCommand(p =>
        {
            if ((p as ForgeMergeRequest ?? SelectedMergeRequest) is { } mr) Shell.OpenUrl(mr.WebUrl);
        });
        CheckoutMergeRequestCommand = Cmd(async _ =>
        {
            var mr = SelectedMergeRequest!;
            await Run($"Check out {mr.Reference}", async a =>
            {
                await a.FetchAsync();
                await a.SwitchAsync($"{Repo.RemoteName}/{mr.SourceBranch}");
            }, $"On '{mr.SourceBranch}'");
        }, _ => SelectedMergeRequest is { State: "opened" });
        ApproveCommand = Cmd(ApproveAsync, _ => SelectedMergeRequest is { State: "opened" });
        ReviewMergeRequestCommand = new RelayCommand(_ =>
        {
            if (SelectedMergeRequest is { } mr && Repo.Remote is { } remote)
                new MergeRequestReviewWindow(Repo.Path, Repo.RemoteName ?? "origin", remote, mr).Show();
        }, _ => SelectedMergeRequest is not null && Repo.Remote is not null);
        DownloadArtifactsCommand = Cmd(async _ =>
        {
            if (SelectedPipeline is not { } pipe || Repo.Remote is not { } remote) return;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", $"{Repo.Name}-pipeline-{pipe.Id}");
            var files = await _main.RunApiAsync("Download artifacts", () => Forges.Require(remote).DownloadArtifactsAsync(remote, pipe.Id, SelectedJob?.Id, folder));
            if (files is null) return;
            _main.AddLog($"Saved {files.Count} artifact file(s) to {folder}", LogLevel.Success);
            Shell.SelectInExplorer(files[0]);
        }, _ => SelectedPipeline is not null);
        MergeCommand = Cmd(MergeAsync, _ => SelectedMergeRequest is { State: "opened", Draft: false, HasConflicts: false });
        MarkReadyCommand = Cmd(ToggleDraftAsync, _ => SelectedMergeRequest is { State: "opened" });
        CreateMergeRequestCommand = Cmd(_ => _main.CreateMergeRequestAsync(Repo), _ => Repo.IsFeatureBranch && Repo.HasForge);
        OpenMergeRequestListCommand = new RelayCommand(() => Shell.OpenUrl(ForgeLinks.MergeRequests(Repo.Remote!)), () => Repo.Remote is not null);

        // ---- pipelines
        RefreshPipelinesCommand = Cmd(_ => LoadPipelinesAsync());
        RetryPipelineCommand = Cmd(_ => PipelineApi("Retry pipeline", (c, r) => c.RetryPipelineAsync(r, SelectedPipeline!.Id)),
            _ => SelectedPipeline is { Kind: "failed" or "canceled" });
        CancelPipelineCommand = Cmd(_ => PipelineApi("Cancel pipeline", (c, r) => c.CancelPipelineAsync(r, SelectedPipeline!.Id)),
            _ => SelectedPipeline is { Kind: "running" or "pending" });
        RunPipelineCommand = Cmd(_ => PipelineApi($"Run {Repo.PipelineTerm}", async (c, r) =>
        {
            var started = await c.RunPipelineAsync(r, Repo.Branch);
            _main.ShowBanner(started, $"on {Repo.Branch}", "success");
        }), _ => Repo.HasUpstream);
        OpenPipelineCommand = new RelayCommand(p =>
        {
            if ((p as ForgePipeline ?? SelectedPipeline) is { } pl) Shell.OpenUrl(pl.WebUrl);
        });
        RetryJobCommand = Cmd(async p =>
        {
            if ((p as ForgeJob ?? SelectedJob) is { } job) await PipelineApi("Retry job", (c, r) => c.RetryJobAsync(r, job.Id));
        });
        PlayJobCommand = Cmd(async p =>
        {
            if ((p as ForgeJob ?? SelectedJob) is { } job) await PipelineApi("Run job", (c, r) => c.PlayJobAsync(r, job.Id));
        }, _ => Repo.IsGitLabServer);
        OpenJobCommand = new RelayCommand(p =>
        {
            if ((p as ForgeJob ?? SelectedJob) is { } job) Shell.OpenUrl(job.WebUrl);
        });
        ReloadJobLogCommand = Cmd(_ => LoadJobLogAsync(), _ => SelectedJob is not null);
        _pipelinePoll.Tick += async (_, _) => await PollPipelinesAsync();

        // ---- stashes
        StashCommand = Cmd(StashAsync, _ => Repo.IsDirty);
        ApplyStashCommand = Cmd(async _ => await Run("Apply stash", a => a.ApplyStashAsync(SelectedStash!.Name, pop: false), "Stash applied"),
            _ => SelectedStash is not null);
        PopStashCommand = Cmd(async _ => await Run("Pop stash", a => a.ApplyStashAsync(SelectedStash!.Name, pop: true), "Stash applied and removed"),
            _ => SelectedStash is not null);
        DropStashCommand = Cmd(async _ =>
        {
            var s = SelectedStash!;
            if (PromptWindow.Confirm("Delete stash", $"Delete {s.Name} \"{s.Message}\"? Its changes are lost.", "Delete", danger: true))
                await Run("Drop stash", a => a.DropStashAsync(s.Name), "Stash deleted");
        }, _ => SelectedStash is not null);

        // ---- worktrees
        OpenWorktreeCommand = Cmd(async _ => await _main.OpenWorktreeAsync(SelectedWorktree!.Path),
            _ => SelectedWorktree is { IsCurrent: false, Prunable: false, Bare: false });
        AddWorktreeCommand = Cmd(AddWorktreeAsync);
        RemoveWorktreeCommand = Cmd(RemoveWorktreeAsync, _ => SelectedWorktree is { IsMain: false, IsCurrent: false });
        PruneWorktreesCommand = Cmd(_ => Run("Prune worktrees", a => a.PruneWorktreesAsync()), _ => Worktrees.Any(w => w.Prunable));

        InitSvn();
    }

    private static AsyncCommand Cmd(Func<object?, Task> run, Func<object?, bool>? can = null) => new(run, can);

    private Task Run(string title, Func<RepoActions, Task> action, string? summary = null) =>
        _main.RunRepoAsync(Repo, title, async a =>
        {
            await action(a);
            return summary ?? string.Empty;
        }, refreshServer: summary is not null);

    // =================================================================================== tabs

    private int _selectedTab;
    private bool _loading;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            OnPropertiesChanged(nameof(ShowTagsTab), nameof(ShowWorktreesTab), nameof(ShowStashesTab), nameof(ShowForgeTabs));
            if (value != TabPipelines) _pipelinePoll.Stop();
            _ = LoadCurrentTabAsync();
        }
    }

    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public void GoToTab(int tab) => SelectedTab = tab;

    private int _tagCount;
    public int TagCount { get => _tagCount; private set { if (Set(ref _tagCount, value)) OnPropertyChanged(nameof(ShowTagsTab)); } }

    // Rarely used tabs only take room when there's something in them (or you went there from More / the palette / Alt+digit).
    public bool ShowTagsTab => TagCount > 0 || SelectedTab == TabTags;
    public bool ShowWorktreesTab => LinkedWorktreeCount > 0 || SelectedTab == TabWorktrees;
    public bool ShowStashesTab => StashCount > 0 || SelectedTab == TabStashes;

    /// <summary>Merge requests and pipelines need a GitHub / GitLab remote (not an SVN working copy or a local-only repo).</summary>
    public bool ShowForgeTabs => Repo.HasForge || SelectedTab is TabMergeRequests or TabPipelines;

    private void Repo_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepoViewModel.Remote) or nameof(RepoViewModel.HasForge)) OnPropertyChanged(nameof(ShowForgeTabs));
    }

    public async Task LoadCurrentTabAsync()
    {
        Loading = true;
        try
        {
            switch (SelectedTab)
            {
                case TabOverview: await LoadOverviewAsync(); break;
                case TabChanges: await LoadChangesAsync(); break;
                case TabBranches: await LoadBranchesAsync(); break;
                case TabHistory: await LoadHistoryAsync(); break;
                case TabMergeRequests: await LoadMergeRequestsAsync(); break;
                case TabPipelines: await LoadPipelinesAsync(); break;
                case TabStashes: await LoadStashesAsync(); break;
                case TabTags: await LoadTagsAsync(); break;
                case TabWorktrees: await LoadWorktreesAsync(); break;
                case TabSvn: await LoadSvnAsync(); break;
            }
        }
        catch (Exception ex)
        {
            _main.AddLog($"{Repo.Name}: loading the tab failed - {ex.Message}", LogLevel.Error);
        }
        finally
        {
            Loading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // =================================================================================== commands

    public ICommand GoToTabCommand { get; }
    public ICommand StageCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand HealthCommand { get; }
    public ICommand ReviewMergeRequestCommand { get; }
    public ICommand DownloadArtifactsCommand { get; }
    public ICommand InteractiveRebaseCommand { get; }
    public ICommand RemotesCommand { get; }

    /// <summary>Rewrite the branch's own commits: since it left the default branch, or (on the default branch)
    /// the unpushed ones plus up to 20 before them.</summary>
    private async Task InteractiveRebaseAsync()
    {
        var mb = await Git.RunAsync(Repo.Path, new[] { "merge-base", "HEAD", Repo.DefaultRef });
        var onto = mb.StdOut.Trim();
        var head = (await Git.RunAsync(Repo.Path, new[] { "rev-parse", "HEAD" })).StdOut.Trim();
        if (!mb.Success || onto.Length == 0 || onto == head)
        {
            var count = int.TryParse((await Git.RunAsync(Repo.Path, new[] { "rev-list", "--count", "HEAD" })).StdOut.Trim(), out var c) ? c : 0;
            if (count < 2)
            {
                await Run("Interactive rebase", _ => throw new UserFacingException("Not enough commits to rewrite."));
                return;
            }
            var back = Math.Min(Math.Max(Repo.Ahead, 0) + 20, count - 1);
            onto = (await Git.RunAsync(Repo.Path, new[] { "rev-parse", $"HEAD~{back}" })).StdOut.Trim();
        }
        var merges = (await Git.RunAsync(Repo.Path, new[] { "rev-list", "--merges", $"{onto}..HEAD" })).StdOut.Trim();
        if (merges.Length > 0)
        {
            await Run("Interactive rebase", _ => throw new UserFacingException("This range contains merge commits.",
                "Interactive rebase here only handles straight history. Use Update from default (rebase) first, or rewrite with git directly."));
            return;
        }
        var commits = await Git.GetLogAsync(Repo.Path, $"{onto}..HEAD", 200);
        if (commits.Count == 0) return;
        commits.Reverse();
        var anyPushed = false;
        if (Repo.HasUpstream)
        {
            var unpushed = await Git.GetUnpushedHashesAsync(Repo.Path, Repo.Upstream!);
            anyPushed = commits.Any(c => !unpushed.Contains(c.Hash));
        }
        var steps = commits.Select(c => new RebaseStep { Hash = c.Hash, OriginalMessage = c.Subject, Message = c.Subject }).ToList();
        var order = steps.Select(s => s.Hash).ToList();
        var w = new InteractiveRebaseWindow(Repo.Branch, onto[..Math.Min(8, onto.Length)], steps, anyPushed);
        if (w.ShowDialog() != true) return;
        var plan = w.Plan.ToList();
        var preview = await RewritePreview.ForInteractiveRebaseAsync(Repo.Path, onto, plan, order);
        if (preview.NothingToDo || !PreviewWindow.Confirm(preview, "Rewrite")) return;
        await Run("Interactive rebase", a => a.InteractiveRebaseAsync(onto, plan), anyPushed ? "Rewritten - Force push to update the server" : "Rewritten");
    }
    public ICommand UnstageCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand DiscardAllCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand CommitPushCommand { get; }
    public ICommand SuggestMessageCommand { get; }
    public ICommand UseTemplateCommand { get; }
    public ICommand UndoLastCommitCommand { get; }
    public ICommand KeepMineCommand { get; }
    public ICommand TakeTheirsCommand { get; }
    public ICommand MarkResolvedCommand { get; }
    public ICommand ResolveConflictCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand AbortCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ShowInExplorerCommand { get; }

    public ICommand ForcePushCommand { get; }
    public ICommand MergeBranchCommand { get; }
    public ICommand RebaseOntoCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand CreateTagCommand { get; }
    public ICommand DeleteTagCommand { get; }
    public ICommand PushTagCommand { get; }
    public ICommand PushAllTagsCommand { get; }
    public ICommand DeleteRemoteTagCommand { get; }
    public ICommand AddToGitIgnoreCommand { get; }
    public ICommand IgnoreFileCommand { get; }
    public ICommand IgnoreExtCommand { get; }
    public ICommand IgnoreFolderCommand { get; }
    public ICommand ViewFileHistoryCommand { get; }
    public ICommand BlameCommand { get; }
    public ICommand PopOutChangeDiffCommand { get; }
    public ICommand PopOutCommitDiffCommand { get; }

    public ICommand SwitchBranchCommand { get; }
    public ICommand CheckoutRemoteCommand { get; }
    public ICommand DeleteBranchCommand { get; }
    public ICommand DeleteRemoteBranchCommand { get; }
    public ICommand RenameBranchCommand { get; }
    public ICommand CleanupBranchesCommand { get; }
    public ICommand OpenBranchCommand { get; }
    public ICommand CompareCommand { get; }

    public ICommand LoadMoreHistoryCommand { get; }
    public ICommand CopyHashCommand { get; }
    public ICommand OpenCommitCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand BranchFromCommitCommand { get; }
    public ICommand CherryPickCommand { get; }

    public ICommand RefreshMergeRequestsCommand { get; }
    public ICommand OpenMergeRequestCommand { get; }
    public ICommand CheckoutMergeRequestCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand MarkReadyCommand { get; }
    public ICommand CreateMergeRequestCommand { get; }
    public ICommand OpenMergeRequestListCommand { get; }

    public ICommand RefreshPipelinesCommand { get; }
    public ICommand RetryPipelineCommand { get; }
    public ICommand CancelPipelineCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand OpenPipelineCommand { get; }
    public ICommand RetryJobCommand { get; }
    public ICommand PlayJobCommand { get; }
    public ICommand OpenJobCommand { get; }
    public ICommand ReloadJobLogCommand { get; }

    public ICommand StashCommand { get; }
    public ICommand ApplyStashCommand { get; }
    public ICommand PopStashCommand { get; }
    public ICommand DropStashCommand { get; }

    public ICommand OpenWorktreeCommand { get; }
    public ICommand AddWorktreeCommand { get; }
    public ICommand RemoveWorktreeCommand { get; }
    public ICommand PruneWorktreesCommand { get; }

    // =================================================================================== helpers

    /// <summary>A file row, or every file under a folder row of the tree view.</summary>
    private static IReadOnlyList<FileChange> ChangesOf(object? p) => p switch
    {
        FileChange f => new[] { f },
        FileTreeFolder d => d.Files.OfType<FileChange>().ToList(),
        _ => Array.Empty<FileChange>(),
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
