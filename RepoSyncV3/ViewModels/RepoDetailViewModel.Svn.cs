using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

/// <summary>A row on the SVN tab: one file that differs from SVN, with the "send this one" tick.</summary>
public sealed class SvnFileItem : ObservableObject, ICheckableTreeFile
{
    /// <summary>Indent level in the tree view (set by FileTreeView).</summary>
    public int TreeDepth { get; set; }
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/'));
    public string Folder => Path.TrimEnd('/') is var p && p.LastIndexOf('/') is > 0 and var i ? p[..i] : string.Empty;

    private bool _selected = true;

    public SvnFileItem(SvnDiffEntry entry) => Entry = entry;

    public SvnDiffEntry Entry { get; }
    public string Path => Entry.Path;
    public string StatusText => Entry.StatusText;
    public string CommittedText => Entry.Uncommitted ? "not committed" : string.Empty;

    /// <summary>Only committed changes can be sent.</summary>
    public bool CanSend => !Entry.Uncommitted;
    public bool Selected { get => _selected && CanSend; set => Set(ref _selected, value); }
    bool ICheckableTreeFile.IsChecked { get => Selected; set => Selected = value; }
    bool ICheckableTreeFile.CanCheck => CanSend;
}

/// <summary>A row in the SVN tab's progress panel: one step of the last Ship / Send / Sync.</summary>
public sealed class SvnStepItem : ObservableObject
{
    private SvnStepState _state;
    private string? _message;

    public SvnStepItem(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Message { get => _message; set => Set(ref _message, value); }

    public SvnStepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value)) OnPropertiesChanged(nameof(Tone), nameof(Glyph), nameof(StateText));
        }
    }

    /// <summary>Pill tone: info while running, success / error when finished, neutral for pending and skipped.</summary>
    public string Tone => State switch
    {
        SvnStepState.Running => "info",
        SvnStepState.Done => "success",
        SvnStepState.Failed => "error",
        _ => string.Empty,
    };

    public string Glyph => State switch
    {
        SvnStepState.Running => "",
        SvnStepState.Done => "",
        SvnStepState.Failed => "",
        SvnStepState.Skipped => "",
        _ => "",
    };

    public string StateText => State switch
    {
        SvnStepState.Running => "running",
        SvnStepState.Done => "done",
        SvnStepState.Failed => "failed",
        SvnStepState.Skipped => "skipped",
        _ => "pending",
    };
}

/// <summary>The SVN tab: the SVN round trip (Sync / Send / Undo) from git-sync-v2, for an SVN working copy
/// whose git 'main' mirrors SVN and its work-branch worktree. Shown on both cards.</summary>
public sealed partial class RepoDetailViewModel
{
    public const int TabSvn = 9;

    private string _svnSummary = string.Empty;
    private List<string> _svnWarnings = new();
    private string? _svnBranch;
    private string? _svnWorktree;
    private SvnFileItem? _selectedSvnFile;
    private List<DiffLine> _svnDiff = new();
    private bool _svnPendingSend;

    public ObservableCollection<string> SvnBranches { get; } = new();
    public ObservableCollection<SvnFileItem> SvnFiles { get; } = new();

    public string SvnSummary { get => _svnSummary; private set => Set(ref _svnSummary, value); }
    public List<string> SvnWarnings { get => _svnWarnings; private set => Set(ref _svnWarnings, value); }
    public bool SvnPendingSend { get => _svnPendingSend; private set => Set(ref _svnPendingSend, value); }
    public string? SvnWorktree { get => _svnWorktree; private set => Set(ref _svnWorktree, value); }

    /// <summary>The work branch to send from / sync into (each linked worktree's branch).</summary>
    public string? SvnBranch
    {
        get => _svnBranch;
        set
        {
            if (Set(ref _svnBranch, value) && SelectedTab == TabSvn) _ = LoadSvnAsync();
        }
    }

    public SvnFileItem? SelectedSvnFile
    {
        get => _selectedSvnFile;
        set
        {
            if (Set(ref _selectedSvnFile, value)) _ = LoadSvnDiffAsync();
        }
    }

    public List<DiffLine> SvnDiff { get => _svnDiff; private set => Set(ref _svnDiff, value); }

    public ICommand SvnSyncCommand { get; private set; } = null!;
    public ICommand SvnPreviewSyncCommand { get; private set; } = null!;
    public ICommand SvnSendCommand { get; private set; } = null!;
    public ICommand SvnSendSelectedCommand { get; private set; } = null!;
    public ICommand SvnPreviewSendCommand { get; private set; } = null!;
    public ICommand SvnUndoCommand { get; private set; } = null!;
    public ICommand SvnRecordCommand { get; private set; } = null!;
    public ICommand SvnMoveWorktreeCommand { get; private set; } = null!;
    public ICommand SvnCommitDialogCommand { get; private set; } = null!;
    public ICommand SvnMergeToolCommand { get; private set; } = null!;
    public ICommand SvnSelectAllCommand { get; private set; } = null!;
    public ICommand SvnSelectNoneCommand { get; private set; } = null!;
    public ICommand SvnShipCommand { get; private set; } = null!;
    public ICommand SvnClearStepsCommand { get; private set; } = null!;
    public ICommand SvnRefreshLogCommand { get; private set; } = null!;
    public ICommand SvnGoToMirrorCommand { get; private set; } = null!;

    // ------------------------------------------------------------------ Ship to SVN + step panel

    private bool _svnInlineCommit;
    private string _svnCommitMessage = string.Empty;
    private string _svnStepsTitle = string.Empty;

    /// <summary>Steps of the last Ship / Send / Sync, as the workflow reports them.</summary>
    public ObservableCollection<SvnStepItem> SvnSteps { get; } = new();
    public string SvnStepsTitle { get => _svnStepsTitle; private set => Set(ref _svnStepsTitle, value); }

    /// <summary>On: Ship commits with 'svn commit -m' and <see cref="SvnCommitMessage"/>; off: the TortoiseSVN dialog.</summary>
    public bool SvnInlineCommit { get => _svnInlineCommit; set => Set(ref _svnInlineCommit, value); }
    public string SvnCommitMessage { get => _svnCommitMessage; set => Set(ref _svnCommitMessage, value); }

    // ------------------------------------------------------------------ SVN log

    public const string SvnViewFiles = "files";
    public const string SvnViewLog = "log";
    private string _svnView = SvnViewFiles;
    private SvnRevision? _selectedSvnRevision;
    private string _svnLogStatus = string.Empty;
    private int _svnLogLimit = 50;

    /// <summary>Sub-view of the SVN tab: "files" (differs from SVN) or "log" (svn log with git mirror links).</summary>
    public string SvnView
    {
        get => _svnView;
        set
        {
            if (Set(ref _svnView, value) && value == SvnViewLog && SvnRevisions.Count == 0) _ = LoadSvnLogAsync();
        }
    }

    public ObservableCollection<SvnRevision> SvnRevisions { get; } = new();
    public SvnRevision? SelectedSvnRevision
    {
        get => _selectedSvnRevision;
        set
        {
            if (!Set(ref _selectedSvnRevision, value)) return;
            SvnRevisionPaths.Clear();
            foreach (var p in value?.Paths ?? new()) SvnRevisionPaths.Add(p);
        }
    }

    /// <summary>The selected revision's changed paths (a collection, so the file tree can follow it).</summary>
    public ObservableCollection<SvnChangedPath> SvnRevisionPaths { get; } = new();

    private FileTreeView<SvnFileItem>? _svnFilesTree;
    private FileTreeView<SvnChangedPath>? _svnRevisionPathsTree;
    public FileTreeView<SvnFileItem> SvnFilesTree => _svnFilesTree ??= new(SvnFiles, _isFileTree);
    public FileTreeView<SvnChangedPath> SvnRevisionPathsTree => _svnRevisionPathsTree ??= new(SvnRevisionPaths, _isFileTree);
    public string SvnLogStatus { get => _svnLogStatus; private set => Set(ref _svnLogStatus, value); }
    public IReadOnlyList<int> SvnLogLimits { get; } = new[] { 25, 50, 100, 250 };

    public int SvnLogLimit
    {
        get => _svnLogLimit;
        set
        {
            if (Set(ref _svnLogLimit, value)) _ = LoadSvnLogAsync();
        }
    }

    private void InitSvn()
    {
        bool Ready(object? _) => Repo.SvnWorkingCopy is not null && SvnBranch is not null;

        SvnShipCommand = Cmd(_ => ShipToSvnAsync(), Ready);
        SvnClearStepsCommand = new RelayCommand(_ => { SvnSteps.Clear(); SvnStepsTitle = string.Empty; }, _ => SvnSteps.Count > 0);
        SvnRefreshLogCommand = Cmd(_ => LoadSvnLogAsync(), _ => Repo.SvnWorkingCopy is not null);
        SvnGoToMirrorCommand = Cmd(async p =>
        {
            if ((p as SvnRevision ?? SelectedSvnRevision)?.MirrorHash is { } hash) await GoToCommitAsync(hash);
        }, p => (p as SvnRevision ?? SelectedSvnRevision)?.IsMirrored == true);

        SvnSyncCommand = Cmd(_ => RunSvn("Sync from SVN", f => f.SyncFromSvnAsync(dryRun: false), showSteps: true), Ready);
        SvnPreviewSyncCommand = Cmd(_ => RunSvn("Sync from SVN (preview)", f => f.SyncFromSvnAsync(dryRun: true)), Ready);
        SvnSendCommand = Cmd(async _ =>
        {
            if (PromptWindow.Confirm("Send work to SVN",
                    $"Apply every change on '{SvnBranch}' to the SVN working copy and open the TortoiseSVN commit dialog?\n\nNothing is committed to SVN until you press OK there.", "Send"))
                await RunSvn("SVN Send", f => f.SendAsync(Array.Empty<string>(), dryRun: false), showSteps: true);
        }, Ready);
        SvnSendSelectedCommand = Cmd(async _ =>
        {
            var paths = SvnFiles.Where(f => f.Selected).Select(f => f.Path).ToList();
            if (PromptWindow.Confirm("Send selected files to SVN",
                    $"Apply {paths.Count} ticked file(s) from '{SvnBranch}' to the SVN working copy and open the TortoiseSVN commit dialog?", "Send"))
                await RunSvn($"SVN Send ({paths.Count} file(s))", f => f.SendAsync(paths, dryRun: false), showSteps: true);
        }, p => Ready(p) && SvnFiles.Any(f => f.Selected) && SvnFiles.Any(f => f.CanSend && !f.Selected));
        SvnPreviewSendCommand = Cmd(_ => RunSvn("SVN Send (preview)", f => f.SendAsync(Array.Empty<string>(), dryRun: true)), Ready);
        SvnUndoCommand = Cmd(async _ =>
        {
            if (PromptWindow.Confirm("Undo last send", "Revert what the last Send put in the SVN working copy (and delete the files it added)?\n\nYour work branch is not touched.", "Undo", danger: true))
                await RunSvn("SVN Undo last send", f => f.UndoAsync());
        }, p => Ready(p) && SvnPendingSend);
        SvnRecordCommand = Cmd(_ => RunSvn("Record SVN on main", f => f.RecordMirrorAsync()), Ready);
        SvnMoveWorktreeCommand = Cmd(async _ =>
        {
            if (SvnWorktree is not { } from) return;
            var target = PromptWindow.Ask("Move work folder",
                $"New folder name (renames it in place) or full path for '{SvnBranch}'.\n\nNow: {from}\n\nClose VS Code and terminals using it first.",
                Path.GetFileName(from), "Move");
            if (string.IsNullOrWhiteSpace(target)) return;
            string? moved = null;
            await RunSvn("Move work folder", async f =>
            {
                moved = await f.MoveWorktreeAsync(target);
                return $"Work folder is now {moved}";
            });
            if (moved is not null && !string.Equals(moved, from, StringComparison.OrdinalIgnoreCase)) _main.MoveRepoPath(from, moved);
        }, p => Ready(p) && SvnWorktree is not null);
        SvnCommitDialogCommand = new RelayCommand(_ =>
        {
            if (Svn.FindTortoiseTool("TortoiseProc.exe") is { } tortoise)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tortoise, $"/command:commit /path:\"{Repo.SvnWorkingCopy}\"") { UseShellExecute = false });
            else _main.AddLog("TortoiseProc.exe was not found (install TortoiseSVN).", LogLevel.Error);
        }, _ => Repo.SvnWorkingCopy is not null);
        SvnMergeToolCommand = Cmd(async _ =>
        {
            if (Compare() is { } c && SelectedSvnFile is { } f && await c.OpenInTortoiseMergeAsync(f.Entry) is { } error)
                _main.AddLog(error, LogLevel.Error);
        }, _ => SelectedSvnFile is not null);
        SvnSelectAllCommand = new RelayCommand(_ => { foreach (var f in SvnFiles) f.Selected = true; });
        SvnSelectNoneCommand = new RelayCommand(_ => { foreach (var f in SvnFiles) f.Selected = false; });
    }

    private SvnCompare? Compare() =>
        Repo.SvnWorkingCopy is { } wc && SvnWorktree is { } wt ? new SvnCompare(wc, wt) : null;

    /// <summary>Runs a workflow step. <paramref name="showSteps"/> resets the progress panel and feeds it the workflow's step reports.</summary>
    private async Task<bool> RunSvn(string title, Func<SvnWorkflow, Task<string>> action, bool showSteps = false)
    {
        var wc = Repo.SvnWorkingCopy!;
        var branch = SvnBranch!;
        IProgress<SvnStepProgress>? steps = null;
        if (showSteps)
        {
            SvnSteps.Clear();
            SvnStepsTitle = title;
            steps = new Progress<SvnStepProgress>(OnSvnStep);
        }
        var ok = await _main.RunRepoAsync(Repo, title, a =>
        {
            var flow = a.SvnFlow(wc, branch);
            flow.Steps = steps;
            return action(flow);
        }, refreshServer: false);
        // Sync/Send change both cards: the working copy (main) and the worktree (work)
        foreach (var other in _main.Repos.Where(r => r != Repo && (string.Equals(r.Path, wc, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r.Path, SvnWorktree, StringComparison.OrdinalIgnoreCase))).ToList())
            await other.RefreshLocalAsync();
        if (showSteps && SvnView == SvnViewLog) await LoadSvnLogAsync();
        return ok;
    }

    private void OnSvnStep(SvnStepProgress p)
    {
        var item = SvnSteps.FirstOrDefault(s => s.Id == p.Id);
        if (item is null) SvnSteps.Add(item = new SvnStepItem(p.Id, p.Title));
        item.State = p.State;
        item.Message = p.Message;
    }

    /// <summary>Ship to SVN: committed branch -> Send -> SVN commit -> Sync, with the step panel showing each one.</summary>
    private async Task ShipToSvnAsync()
    {
        if (SvnWorktree is not { } worktree) return;

        // the branch must be committed: offer its Changes tab instead of failing half-way
        var status = await Git.RunAsync(worktree, new[] { "status", "--porcelain" });
        if (status.Success && !string.IsNullOrWhiteSpace(status.StdOut))
        {
            var count = status.StdOut.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (PromptWindow.Confirm("Commit your work first",
                    $"'{SvnBranch}' has {count} uncommitted change(s) in {worktree}.\n\nShip to SVN only sends committed work. Open the Changes tab to commit them?", "Open Changes"))
                OpenWorktreeChanges(worktree);
            return;
        }

        string? message = null;
        if (SvnInlineCommit)
        {
            message = SvnCommitMessage.Trim();
            if (message.Length == 0)
            {
                _main.AddLog("Ship to SVN: type the SVN commit message first (or turn off 'Commit message here' to use the TortoiseSVN dialog).", LogLevel.Warning);
                return;
            }
        }

        var how = message is null
            ? "open the TortoiseSVN commit dialog, and once you commit there, sync back from SVN"
            : $"commit to SVN with the message below, and sync back from SVN.\n\n{message}";
        if (!PromptWindow.Confirm("Ship to SVN", $"Apply every change on '{SvnBranch}' to the SVN working copy, {how}", "Ship")) return;

        // step reports arrive posted, so the outcome text (not the panel) tells whether it reached the end
        string? outcome = null;
        await RunSvn("Ship to SVN", async f => outcome = await f.ShipAsync(message), showSteps: true);
        if (message is not null && outcome?.StartsWith("Matches SVN", StringComparison.Ordinal) == true) SvnCommitMessage = string.Empty;
    }

    /// <summary>The work branch's Changes tab: this card if it is the worktree, otherwise the worktree's own card.</summary>
    private void OpenWorktreeChanges(string worktree)
    {
        if (string.Equals(Repo.Path, worktree, StringComparison.OrdinalIgnoreCase))
        {
            GoToTab(TabChanges);
            return;
        }
        if (_main.Repos.FirstOrDefault(r => string.Equals(r.Path, worktree, StringComparison.OrdinalIgnoreCase)) is { } card)
        {
            _main.SelectedRepo = card;
            _main.Detail?.GoToTab(TabChanges);
        }
        else _main.AddLog($"{worktree} isn't in the repo list: add it (or commit there in your editor), then Ship again.", LogLevel.Warning);
    }

    // ------------------------------------------------------------------ SVN log

    private async Task LoadSvnLogAsync()
    {
        if (Repo.SvnWorkingCopy is not { } wc) return;
        SvnLogStatus = "Reading svn log...";
        try
        {
            var revisions = await SvnLog.GetLogAsync(wc, SvnLogLimit);
            SvnLog.MapMirrors(revisions, await SvnLog.GetMirrorTagsAsync(wc));
            var keep = SelectedSvnRevision?.Revision;
            Replace(SvnRevisions, revisions);
            SelectedSvnRevision = SvnRevisions.FirstOrDefault(r => r.Revision == keep) ?? SvnRevisions.FirstOrDefault();
            var waiting = revisions.Count(r => !r.IsMirrored);
            SvnLogStatus = $"{revisions.Count} latest revision(s)"
                           + (waiting > 0 ? $", {waiting} not on git main yet (Sync from SVN)" : ", all on git main");
        }
        catch (UserFacingException ex)
        {
            SvnLogStatus = $"{ex.Message} {string.Join(" ", ex.Help.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()))}";
        }
    }

    /// <summary>Opens the History tab on <paramref name="hash"/>: the current list if it's there, else all branches,
    /// else that commit alone (older than the loaded list).</summary>
    public async Task GoToCommitAsync(string hash)
    {
        _historyFilter = string.Empty;
        _searchResults = null;
        OnPropertyChanged(nameof(HistoryFilter));
        if (_selectedTab != TabHistory)
        {
            _selectedTab = TabHistory;
            OnPropertiesChanged(nameof(SelectedTab), nameof(ShowTagsTab), nameof(ShowWorktreesTab));
        }

        await LoadHistoryAsync();
        var hit = Commits.FirstOrDefault(c => c.Hash == hash);
        if (hit is null && _historyScope != "all")
        {
            _historyScope = "all";
            OnPropertyChanged(nameof(HistoryScope));
            await LoadHistoryAsync();
            hit = Commits.FirstOrDefault(c => c.Hash == hash);
        }
        if (hit is null)
        {
            _searchResults = await Git.GetLogAsync(Repo.Path, hash, 1);
            ApplyHistoryFilter();
            hit = Commits.FirstOrDefault();
        }
        SelectedCommit = hit;
    }

    private async Task LoadSvnAsync()
    {
        if (Repo.SvnWorkingCopy is not { } wc) return;
        if (SvnView == SvnViewLog) _ = LoadSvnLogAsync();

        var linked = (await Git.GetWorktreesAsync(wc)).Where(w => !w.IsMain && !w.Prunable && w.Branch is not null).ToList();
        var branches = linked.Select(w => w.Branch!).ToList();
        if (!branches.SequenceEqual(SvnBranches)) Replace(SvnBranches, branches);
        if (_svnBranch is null || !branches.Contains(_svnBranch))
        {
            // the worktree card sends its own branch; the working copy card picks 'work' if there is one
            var own = linked.FirstOrDefault(w => string.Equals(w.Path, Repo.Path, StringComparison.OrdinalIgnoreCase))?.Branch;
            _svnBranch = own ?? branches.FirstOrDefault(b => b == "work") ?? branches.FirstOrDefault();
            OnPropertyChanged(nameof(SvnBranch));
        }
        SvnWorktree = linked.FirstOrDefault(w => w.Branch == _svnBranch)?.Path;
        SvnPendingSend = File.Exists(Path.Combine(wc, ".git", "git-sync-last-send.json"));

        var keep = SelectedSvnFile?.Path;
        if (Compare() is not { } compare)
        {
            SvnSummary = "No work branch: add a worktree for it on the Worktrees tab (e.g. 'work').";
            SvnWarnings = new List<string>();
            SvnFiles.Clear();
            SvnDiff = new List<DiffLine>();
            return;
        }

        var (description, warnings) = await compare.GetStateAsync();
        var files = await compare.GetChangesAsync();
        Replace(SvnFiles, files.Select(f => new SvnFileItem(f)));
        var toSend = files.Count(f => !f.Uncommitted);
        var notCommitted = files.Count - toSend;
        SvnSummary = $"{description}  vs  '{_svnBranch}' ({SvnWorktree}):  {toSend} file(s) to send"
                     + (notCommitted > 0 ? $", {notCommitted} not committed yet" : string.Empty)
                     + (SvnPendingSend ? "  -  a Send is waiting to be committed in SVN" : string.Empty);
        SvnWarnings = warnings;
        SelectedSvnFile = SvnFiles.FirstOrDefault(f => f.Path == keep) ?? SvnFiles.FirstOrDefault();
        if (SelectedSvnFile is null) SvnDiff = new List<DiffLine>();
    }

    private async Task LoadSvnDiffAsync()
    {
        SvnDiff = SelectedSvnFile is not null && Compare() is { } c ? await c.GetDiffAsync(SelectedSvnFile.Entry) : new List<DiffLine>();
    }
}
