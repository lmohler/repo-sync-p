using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

/// <summary>One repo in the Commit all dialog: whether to commit it, and with what message.</summary>
public sealed class CommitAllRow : ObservableObject
{
    private bool _include;
    private string _message;

    public CommitAllRow(RepoViewModel repo, string message, string? blockedReason, string? note)
    {
        Repo = repo;
        _message = message;
        BlockedReason = blockedReason;
        Note = note;
        _include = blockedReason is null;
    }

    public RepoViewModel Repo { get; }
    public string Name => Repo.Name;
    public string Branch => Repo.Detached ? "(detached)" : Repo.Branch;
    public string ChangesText => $"{Repo.ChangeCount} change(s)";

    /// <summary>Why this repo can't be committed from here (it still shows, unticked), or null.</summary>
    public string? BlockedReason { get; }
    public bool CanInclude => BlockedReason is null;

    /// <summary>A warning that doesn't block (identity check set to warn).</summary>
    public string? Note { get; }

    public bool Include { get => _include; set => Set(ref _include, value && CanInclude); }
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Why a repo can't be part of Commit all; null when it can.</summary>
    public static string? Blocked(RepoViewModel r, string identityCheck) =>
        Svn.IsWorkingCopy(r.Path) ? "SVN working copy - commit on its work folder's card"
        : r.InProgress || r.ConflictCount > 0 ? $"{(r.ConflictCount > 0 ? "conflicts" : r.InProgressText.ToLowerInvariant())} - finish it on the Changes tab"
        : r.Detached ? "detached HEAD - start or switch to a branch first"
        : r.IdentityMismatch && identityCheck == "block" ? $"commits would use {r.GitEmail}, not the {r.WorkspaceName} identity - fix it first"
        : null;

    /// <summary>The suggested message from the changed files, with the branch's ticket in front.</summary>
    public static string Suggest(RepoViewModel r, RepoStatusFiles files)
    {
        var message = Git.SuggestMessage(files.Staged.Concat(files.Unstaged));
        return Git.TicketFromBranch(r.Branch) is { } t && !message.Contains(t, StringComparison.OrdinalIgnoreCase) ? $"{t} {message}" : message;
    }
}

/// <summary>The changed files a suggestion is made from.</summary>
public sealed record RepoStatusFiles(IReadOnlyList<Models.FileChange> Staged, IReadOnlyList<Models.FileChange> Unstaged);
