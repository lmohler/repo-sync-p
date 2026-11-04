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
    // =================================================================================== changes

    public ObservableCollection<FileChange> Staged { get; } = new();
    public ObservableCollection<FileChange> Unstaged { get; } = new();
    public ObservableCollection<FileChange> Conflicted { get; } = new();

    private FileChange? _selectedChange;
    private List<DiffLine> _changeDiff = new();
    private string _commitMessage = string.Empty;
    private bool _amend;

    public FileChange? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (Set(ref _selectedChange, value)) _ = LoadChangeDiffAsync();
        }
    }

    private bool _isSplitDiff;
    public bool IsSplitDiff
    {
        get => _isSplitDiff;
        set
        {
            if (Set(ref _isSplitDiff, value))
            {
                _main.Settings.SplitDiff = value;
                _main.SaveSettings();
                OnPropertyChanged(nameof(ChangeSplitDiff));
                OnPropertyChanged(nameof(CommitSplitDiff));
            }
        }
    }

    // ---- list / tree view of the changed and committed files (one setting for both tabs)

    private bool _isFileTree;
    private FileTreeView<FileChange>? _unstagedTree, _stagedTree;
    private FileTreeView<CommitFile>? _commitFilesTree;

    public FileTreeView<FileChange> UnstagedTree => _unstagedTree ??= new(Unstaged, _isFileTree);
    public FileTreeView<FileChange> StagedTree => _stagedTree ??= new(Staged, _isFileTree);
    public FileTreeView<CommitFile> CommitFilesTree => _commitFilesTree ??= new(CommitFiles, _isFileTree);

    /// <summary>Group the file lists by folder. Saved, and shared by Changes, History and SVN.</summary>
    public bool IsFileTree
    {
        get => _isFileTree;
        set
        {
            if (!Set(ref _isFileTree, value)) return;
            UnstagedTree.IsTree = StagedTree.IsTree = CommitFilesTree.IsTree = SvnFilesTree.IsTree = SvnRevisionPathsTree.IsTree = value;
            _main.Settings.FileTree = value;
            _main.SaveSettings();
        }
    }

    public List<DiffLine> ChangeDiff
    {
        get => _changeDiff;
        private set
        {
            if (Set(ref _changeDiff, value))
                OnPropertyChanged(nameof(ChangeSplitDiff));
        }
    }

    public List<SideBySideDiffRow> ChangeSplitDiff => SideBySideDiffRow.FromUnified(ChangeDiff);

    public string CommitMessage
    {
        get => _commitMessage;
        set { if (Set(ref _commitMessage, value)) OnPropertiesChanged(nameof(SubjectHint), nameof(SubjectTone)); }
    }

    private string Subject => (CommitMessage ?? string.Empty).Split('\n')[0].TrimEnd('\r');

    /// <summary>The 50/72 rule: subject length, and a blank second line before the body.</summary>
    public string SubjectHint
    {
        get
        {
            var lines = (CommitMessage ?? string.Empty).Split('\n');
            if (Subject.Length == 0) return string.Empty;
            if (lines.Length > 1 && lines[1].Trim().Length > 0) return $"Subject {Subject.Length}/50 · leave line 2 blank";
            return Subject.Length switch
            {
                <= 50 => $"Subject {Subject.Length}/50",
                <= 72 => $"Subject {Subject.Length}/50 · a bit long",
                _ => $"Subject {Subject.Length}/50 · too long (over 72) - move details to the body",
            };
        }
    }

    public string SubjectTone => Subject.Length > 72 ? "error" : Subject.Length > 50 ? "warning" : "muted";

    private string? _templateText;
    public bool HasTemplate => _templateText is not null;

    /// <summary>git's commit.template for this repo (comment lines dropped), or null.</summary>
    private async Task LoadTemplateAsync()
    {
        var path = (await Git.RunAsync(Repo.Path, new[] { "config", "--path", "commit.template" })).StdOut.Trim();
        if (path.Length > 0 && !Path.IsPathRooted(path)) path = Path.Combine(Repo.Path, path);
        _templateText = path.Length > 0 && File.Exists(path)
            ? string.Join("\n", File.ReadAllLines(path).Where(l => !l.StartsWith('#'))).Trim()
            : null;
        OnPropertyChanged(nameof(HasTemplate));
    }

    /// <summary>Ticket key from the branch name (PROJ-123); prefixed to the commit message when <see cref="UseTicketPrefix"/>.</summary>
    public string? Ticket => Git.TicketFromBranch(Repo.Branch);
    public bool HasTicket => Ticket is not null;
    private bool _useTicketPrefix = true;
    public bool UseTicketPrefix { get => _useTicketPrefix; set => Set(ref _useTicketPrefix, value); }

    private string WithTicket(string message) =>
        UseTicketPrefix && Ticket is { } t && !string.IsNullOrWhiteSpace(message) && !message.Contains(t, StringComparison.OrdinalIgnoreCase)
            ? $"{t} {message.TrimStart()}" : message;

    public bool Amend
    {
        get => _amend;
        set
        {
            if (!Set(ref _amend, value)) return;
            if (value && string.IsNullOrWhiteSpace(CommitMessage)) CommitMessage = Repo.HeadSubject;
        }
    }

    public bool CanCommit => !Repo.IsBusy && (Staged.Count + Unstaged.Count > 0 || Amend) && Conflicted.Count == 0;

    public string CommitHint => Staged.Count > 0
        ? $"Commits the {Staged.Count} staged file(s)."
        : Unstaged.Count > 0 ? $"Nothing staged - commits all {Unstaged.Count} change(s)." : "No changes to commit.";

    private async Task LoadChangesAsync()
    {
        if (!Directory.Exists(Repo.Path)) return;
        var status = await Git.GetStatusAsync(Repo.Path);
        if (!status.Ok) return;
        var keep = SelectedChange;
        Replace(Staged, status.Staged);
        Replace(Unstaged, status.Unstaged);
        Replace(Conflicted, status.Conflicted);
        OnPropertiesChanged(nameof(CommitHint), nameof(CanCommit), nameof(Ticket), nameof(HasTicket));
        await LoadTemplateAsync();

        // keep the same file selected (on whichever side it is now) so the diff doesn't jump
        var again = keep is null ? null
            : Conflicted.FirstOrDefault(f => f.Path == keep.Path)
              ?? (keep.Staged ? Staged : Unstaged).FirstOrDefault(f => f.Path == keep.Path)
              ?? Unstaged.Concat(Staged).FirstOrDefault(f => f.Path == keep.Path);
        _selectedChange = again ?? Conflicted.FirstOrDefault() ?? Unstaged.FirstOrDefault() ?? Staged.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedChange));
        await LoadChangeDiffAsync();
    }

    private bool _ignoreWhitespace;
    public bool IgnoreWhitespace
    {
        get => _ignoreWhitespace;
        set
        {
            if (Set(ref _ignoreWhitespace, value))
            {
                _ = LoadChangeDiffAsync();
                _ = LoadCommitDiffAsync();
                _ = LoadStashDiffAsync();
            }
        }
    }

    private async Task LoadChangeDiffAsync()
    {
        ChangeDiff = SelectedChange is null ? new List<DiffLine>() : await Git.GetFileDiffAsync(Repo.Path, SelectedChange, IgnoreWhitespace);
    }

    /// <summary>Stage / unstage / discard only some lines of the selected file. Hunk header = the whole hunk.
    /// <paramref name="mode"/>: "stage" / "unstage" (only acts on the matching side), "discard", or "toggle"
    /// (stage an unstaged file's lines, unstage a staged file's).</summary>
    public async Task ApplyLinesAsync(IEnumerable<DiffLine> picked, string mode)
    {
        var change = SelectedChange;
        if (change is null || change.IsConflict) return;
        if ((mode == "stage" && change.Staged) || (mode == "unstage" && !change.Staged)) return;
        if (IgnoreWhitespace)
        {
            await Run("Stage lines", _ => throw new UserFacingException("Turn off 'Ignore whitespace' to stage single lines.", "Line staging works on the real diff."));
            return;
        }
        var set = new HashSet<(int, int)>();
        foreach (var l in picked)
        {
            if (change.IsUntracked)
            {
                // the new-file view isn't a git diff: line N of the file is index N-1 of the --no-index diff's one hunk
                if (l.Kind == DiffKind.Add && int.TryParse(l.NewNo, out var n)) set.Add((0, n - 1));
            }
            else if (l.Kind == DiffKind.Hunk)
                foreach (var h in ChangeDiff.Where(d => d.Hunk == l.Hunk && d.IsChange)) set.Add((h.Hunk, h.Index));
            else if (l.IsChange) set.Add((l.Hunk, l.Index));
        }
        if (set.Count == 0) return;
        var raw = change.IsUntracked
            ? await PartialPatch.RawNewFileDiffAsync(Repo.Path, change.Path)
            : await Git.GetRawFileDiffAsync(Repo.Path, change);
        // unstage and discard are both applied with -R, so they share the "reverse" shape
        var patch = PartialPatch.Build(raw, set, reverse: change.Staged || mode == "discard");
        if (patch is null) return;
        switch (mode)
        {
            case "discard":
                if (change.Staged) return;
                if (!PromptWindow.Confirm("Discard lines", $"Throw away {set.Count} changed line(s) in '{change.Path}'? This can't be undone.", "Discard", danger: true)) return;
                await Run("Discard lines", a => a.ApplyPatchAsync(patch, reverse: false, toWorktree: true));
                break;
            default:
                await Run(change.Staged ? "Unstage lines" : "Stage lines", a => a.ApplyPatchAsync(patch, reverse: change.Staged));
                break;
        }
    }

    private async Task DiscardAsync(IReadOnlyList<FileChange> files)
    {
        if (files.Count == 0) return;
        var what = files.Count == 1 ? $"'{files[0].Path}'" : $"{files.Count} files";
        var newFiles = files.Count(f => f.IsUntracked);
        var text = $"Throw away your changes to {what}? This can't be undone." + (newFiles > 0 ? $"\n\n{newFiles} new file(s) will be deleted." : string.Empty);
        if (!PromptWindow.Confirm("Discard changes", text, "Discard", danger: true)) return;
        await Run("Discard", a => a.DiscardAsync(files));
    }

    private async Task CommitAsync(bool push)
    {
        if (!await _main.CheckIdentityAsync(Repo, push ? "commit and push" : "commit")) return;
        var message = WithTicket(CommitMessage);
        var stageAll = Staged.Count == 0;
        var amend = Amend;
        var ok = await _main.RunRepoAsync(Repo, push ? "Commit & push" : "Commit", async a =>
        {
            if (stageAll) await a.StageAllAsync();
            await a.CommitAsync(message, amend);
            if (!push) return "Committed";
            return "Committed; " + await a.PushAsync();
        }, refreshServer: push);
        if (ok)
        {
            CommitMessage = string.Empty;
            Amend = false;
        }
    }

    private void SuggestMessage()
    {
        var files = Staged.Count > 0 ? Staged : Unstaged;
        CommitMessage = Git.SuggestMessage(files);
    }
}
