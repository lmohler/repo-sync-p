using System.IO;
using System.Windows.Media;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.ViewModels;

/// <summary>The headline state of a repo, worst first; drives the card's color stripe and icon.</summary>
public enum RepoState
{
    Unknown,
    Clean,
    Ahead,
    Unpublished,
    Dirty,
    Behind,
    Diverged,
    Conflict,
    Error,
    Missing,
}

/// <summary>The one thing the app suggests doing next, with the button that does it.</summary>
/// <param name="Action">Key handled by <see cref="MainViewModel.RunNextStepAsync"/>.</param>
/// <param name="Tone">success, info, warning or error (picks the colors).</param>
public sealed record NextStep(string Title, string Detail, string Action, string Button, string Tone);

/// <summary>One repo in the sidebar: its workspace, its local git state, its server state (GitHub or GitLab),
/// and what that adds up to.</summary>
public sealed class RepoViewModel : ObservableObject
{
    public RepoEntry Entry { get; }

    public RepoViewModel(RepoEntry entry)
    {
        Entry = entry;
    }

    public string Path => Entry.Path;
    public string Name => string.IsNullOrWhiteSpace(Entry.DisplayName) ? System.IO.Path.GetFileName(Path.TrimEnd('\\')) : Entry.DisplayName!;
    public string ParentFolder => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? string.Empty;

    private string? _worktreeOf;
    private bool _worktreeChecked;

    /// <summary>For a linked worktree, the main repo folder it belongs to (read from its .git file); null otherwise.</summary>
    public string? WorktreeOf
    {
        get
        {
            if (!_worktreeChecked)
            {
                _worktreeOf = Git.MainWorktreePath(Path);
                _worktreeChecked = true;
            }
            return _worktreeOf;
        }
    }

    public bool IsWorktree => WorktreeOf is not null;

    /// <summary>The SVN working copy this card belongs to (the folder itself, or the main repo of its worktree),
    /// for the SVN tab; null for plain git repos.</summary>
    public string? SvnWorkingCopy => Services.Svn.WorkingCopyFor(Path, WorktreeOf);
    public bool IsSvn => SvnWorkingCopy is not null;

    /// <summary>The SVN working copy itself: its git 'main' only records SVN, so nothing is committed here by hand.</summary>
    public bool IsSvnMirror => Services.Svn.IsWorkingCopy(Path);
    public string WorktreeBadge => WorktreeOf is null ? string.Empty : "worktree of " + System.IO.Path.GetFileName(WorktreeOf);

    public bool Pinned
    {
        get => Entry.Pinned;
        set
        {
            if (Entry.Pinned == value) return;
            Entry.Pinned = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Background fetch / server refresh includes this repo. Unwatched repos only touch the network on demand.</summary>
    public bool Watched
    {
        get => Entry.Watched ?? true;
        set
        {
            if (Watched == value) return;
            Entry.Watched = value;
            OnPropertyChanged();
        }
    }

    public void RenameDisplay(string? name)
    {
        Entry.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        OnPropertiesChanged(nameof(Name), nameof(Monogram), nameof(MonogramBrush));
    }

    // ------------------------------------------------------------------ monogram (card avatar)

    /// <summary>Up to two letters from the repo name ("repo-sync-v3" -> "RS", "TimeTracking" -> "TT").</summary>
    public string Monogram
    {
        get
        {
            var words = System.Text.RegularExpressions.Regex.Matches(Name, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")
                .Select(m => m.Value).Where(w => !char.IsDigit(w[0]) && !(w.Length <= 2 && w[0] is 'v' or 'V')).ToList();
            if (words.Count == 0) return Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
            if (words.Count > 1) return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            var w = words[0];
            return w.Length == 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..2].ToLowerInvariant();
        }
    }

    /// <summary>The repo's own avatar color (repo settings), else one picked from the name - stable across runs.</summary>
    public SolidColorBrush MonogramBrush => Workspaces.Brush(RepoColors.Resolve(Name, Entry.Color));

    /// <summary>Sets (or clears, with null / not a color) the hand-picked avatar color.</summary>
    public void SetColor(string? color)
    {
        Entry.Color = RepoColors.Normalize(color);
        OnPropertyChanged(nameof(MonogramBrush));
    }

    // ------------------------------------------------------------------ profile (stored as workspace)

    public string WorkspaceId => Workspaces.Exists(Entry.Workspace) ? Entry.Workspace! : Workspaces.All[0].Id;
    public WorkspaceSettings Workspace => Workspaces.Get(WorkspaceId);
    public string WorkspaceName => Workspace.Name;
    public int WorkspaceOrder => Workspaces.Order(WorkspaceId);
    public SolidColorBrush WorkspaceBrush => Workspaces.Brush(Workspace.Color);
    public SolidColorBrush WorkspaceTint => Workspaces.Brush(Workspace.Color, 0x30);

    /// <summary>"Work · GitLab", "Personal · GitHub", "Personal · no server".</summary>
    public string WorkspaceLabel => $"{WorkspaceName} · {(Remote is null ? "no server" : Remote.ServerName)}";

    /// <summary>Moves the repo to another profile (an unknown id is ignored).</summary>
    public void SetWorkspace(string? id)
    {
        if (!Workspaces.Exists(id)) return;
        Entry.Workspace = id;
        RaiseWorkspace();
    }

    /// <summary>After a workspace's name / color / identity changed in Settings.</summary>
    public void RaiseWorkspace()
    {
        OnPropertiesChanged(nameof(WorkspaceId), nameof(Workspace), nameof(WorkspaceName), nameof(WorkspaceOrder),
            nameof(WorkspaceBrush), nameof(WorkspaceTint), nameof(WorkspaceLabel));
        RaiseDerived();
    }

    // ------------------------------------------------------------------ git identity

    private string _gitName = string.Empty;
    private string _gitEmail = string.Empty;
    private bool _hasLocalIdentity;

    /// <summary>user.name / user.email git will use for commits here (repo config, else global).</summary>
    public string GitName { get => _gitName; private set => Set(ref _gitName, value); }
    public string GitEmail { get => _gitEmail; private set => Set(ref _gitEmail, value); }

    /// <summary>The repo has its own user.email (set by "Use workspace identity").</summary>
    public bool HasLocalIdentity { get => _hasLocalIdentity; private set => Set(ref _hasLocalIdentity, value); }

    /// <summary>The workspace says which email to commit with, and this repo would use another one.</summary>
    public bool IdentityMismatch =>
        Loaded && !_missing && Workspaces.Settings.IdentityCheck != "off"
        && Workspace.GitEmail.Trim().Length > 0
        && !string.Equals(GitEmail.Trim(), Workspace.GitEmail.Trim(), StringComparison.OrdinalIgnoreCase);

    public string IdentityText => GitEmail.Length == 0 ? "no git identity set" : $"{GitName} <{GitEmail}>";

    public string IdentityProblem => IdentityMismatch
        ? $"Commits here would be authored as {(GitEmail.Length == 0 ? "nobody (no email set)" : GitEmail)}, but this is a {WorkspaceName} repo ({Workspace.GitEmail})."
        : string.Empty;

    // ------------------------------------------------------------------ local state

    private bool _loaded;
    private bool _missing;
    private string? _error;
    private string _branch = string.Empty;
    private bool _detached;
    private string? _upstream;
    private bool _upstreamGone;
    private int _ahead;
    private int _behind;
    private int _staged;
    private int _unstaged;
    private int _untracked;
    private int _conflicts;
    private bool _mergeInProgress;
    private bool _rebaseInProgress;
    private string? _remoteName;
    private string? _defaultBranch;
    private int _aheadOfDefault;
    private int _behindDefault;
    private DateTime? _lastFetch;
    private string _headSubject = string.Empty;
    private DateTime? _headDate;
    private ForgeRemote? _remote;

    public bool Loaded { get => _loaded; private set => Set(ref _loaded, value); }

    private bool _isStale;

    /// <summary>Showing what was cached at the last exit; the real refresh hasn't finished yet.</summary>
    public bool IsStale { get => _isStale; private set { if (Set(ref _isStale, value)) OnPropertyChanged(nameof(StaleText)); } }

    public string StaleText => IsStale && _cachedAt is { } t ? $"Last known status ({TimeText.Relative(t)}) - refreshing..." : string.Empty;
    private DateTime? _cachedAt;
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string Branch { get => _branch; private set => Set(ref _branch, value); }
    public bool Detached { get => _detached; private set => Set(ref _detached, value); }
    public string? Upstream { get => _upstream; private set => Set(ref _upstream, value); }
    public bool UpstreamGone { get => _upstreamGone; private set => Set(ref _upstreamGone, value); }
    public int Ahead { get => _ahead; private set => Set(ref _ahead, value); }
    public int Behind { get => _behind; private set => Set(ref _behind, value); }
    public int StagedCount { get => _staged; private set => Set(ref _staged, value); }
    public int UnstagedCount { get => _unstaged; private set => Set(ref _unstaged, value); }
    public int UntrackedCount { get => _untracked; private set => Set(ref _untracked, value); }
    public int ConflictCount { get => _conflicts; private set => Set(ref _conflicts, value); }
    public bool MergeInProgress { get => _mergeInProgress; private set => Set(ref _mergeInProgress, value); }
    public bool RebaseInProgress { get => _rebaseInProgress; private set => Set(ref _rebaseInProgress, value); }
    public string? RemoteName { get => _remoteName; private set => Set(ref _remoteName, value); }
    public string? DefaultBranch { get => _defaultBranch; private set => Set(ref _defaultBranch, value); }
    public int AheadOfDefault { get => _aheadOfDefault; private set => Set(ref _aheadOfDefault, value); }
    public int BehindDefault { get => _behindDefault; private set => Set(ref _behindDefault, value); }
    public DateTime? LastFetch { get => _lastFetch; private set => Set(ref _lastFetch, value); }
    public string HeadSubject { get => _headSubject; private set => Set(ref _headSubject, value); }
    public DateTime? HeadDate { get => _headDate; private set => Set(ref _headDate, value); }

    public ForgeRemote? Remote
    {
        get => _remote;
        private set
        {
            if (!Set(ref _remote, value)) return;
            OnPropertiesChanged(nameof(ServerHost), nameof(ServerName), nameof(MrTerm), nameof(MrsTerm), nameof(MrShort), nameof(PipelinesTerm),
                nameof(PipelineTerm), nameof(IsGitLabServer), nameof(IsGitHubServer), nameof(CreateMrText), nameof(MrHeading),
                nameof(PipelineHeading), nameof(TokenHint), nameof(CreateMrLongText), nameof(NoMrText), nameof(UpdateFromDefaultHint));
            RaiseWorkspace();
        }
    }

    /// <summary>Overview grouping: the server's host name, or "no server".</summary>
    public string ServerHost => Remote?.Host ?? "no server";

    /// <summary>Overview grouping: needs you, local work (changes / to push), or up to date.</summary>
    public string StatusGroup => !Loaded ? "Loading" : NeedsAttention ? "Needs you" : IsDirty || Ahead > 0 || State == RepoState.Unpublished ? "Local work" : "Up to date";

    public int ChangeCount => StagedCount + UnstagedCount + ConflictCount;
    public bool IsDirty => ChangeCount > 0;
    public bool OnDefaultBranch => DefaultBranch is not null && Branch == DefaultBranch;
    public bool IsFeatureBranch => !OnDefaultBranch && !Detached && Branch.Length > 0;
    public bool HasUpstream => Upstream is not null && !UpstreamGone;

    /// <summary>The remote is on GitHub or GitLab (merge requests, pipelines and web links work).</summary>
    public bool HasForge => Remote is not null;
    public bool IsGitLabServer => Remote?.Kind == ForgeKind.GitLab;
    public bool IsGitHubServer => Remote?.Kind == ForgeKind.GitHub;
    public bool HasToken => Remote is not null && TokenStore.Has(Remote.Host);
    public string LastFetchText => LastFetch is null ? "never fetched" : "fetched " + TimeText.Relative(LastFetch);
    public string HeadDateText => TimeText.Relative(HeadDate);
    public string DefaultRef => RemoteName is null || DefaultBranch is null ? "main" : $"{RemoteName}/{DefaultBranch}";
    public bool InProgress => MergeInProgress || RebaseInProgress;
    public string InProgressText => RebaseInProgress ? "Rebase in progress" : MergeInProgress ? "Merge in progress" : string.Empty;

    // words that differ between GitHub and GitLab
    public string ServerName => ForgeTerms.Server(Remote?.Kind);
    public string MrTerm => ForgeTerms.Mr(Remote?.Kind);
    public string MrsTerm => ForgeTerms.Mrs(Remote?.Kind);
    public string MrShort => ForgeTerms.MrShort(Remote?.Kind);
    public string PipelineTerm => ForgeTerms.Pipeline(Remote?.Kind);
    public string PipelinesTerm => ForgeTerms.Pipelines(Remote?.Kind);
    public string CreateMrText => $"Create {MrShort}";
    public string CreateMrLongText => $"Create {MrTerm}";
    public string NoMrText => $"No {MrTerm} for this branch.";
    public string NewOnDefaultText => $"new on {DefaultBranch ?? "main"}";
    public string UpdateFromDefaultHint => $"Keep the second number low: update from {DefaultBranch ?? "main"} now and then so your {MrTerm} merges cleanly.";
    public string MrHeading => MrTerm.ToUpperInvariant();
    public string PipelineHeading => IsGitHubServer ? "LATEST CHECKS ON THIS BRANCH" : "LATEST PIPELINE ON THIS BRANCH";
    public string TokenHint => $"Add a {ServerName} token (key button, top right) to see {MrTerm}s and {(IsGitHubServer ? "Actions" : "pipelines")}.";

    public string PullText => Behind > 0 ? $"Pull ({Behind})" : "Pull";
    public string PushText => Ahead > 0 ? $"Push ({Ahead})" : !HasUpstream && IsFeatureBranch ? "Publish" : "Push";
    public string UpdateText => BehindDefault > 0 ? $"Update from {DefaultBranch} ({BehindDefault})" : $"Update from {DefaultBranch ?? "main"}";

    public string TrackingText => Detached ? "detached HEAD"
        : Upstream is null ? "not published to the server"
        : UpstreamGone ? $"{Upstream} was deleted on the server"
        : Ahead == 0 && Behind == 0 ? $"up to date with {Upstream}"
        : $"{Upstream}: {Ahead} to push, {Behind} to pull";

    // ------------------------------------------------------------------ server state

    private ForgeMergeRequest? _mergeRequest;
    private ForgePipeline? _pipeline;
    private string? _serverError;
    private DateTime? _serverRefreshed;

    public ForgeMergeRequest? MergeRequest
    {
        get => _mergeRequest;
        private set
        {
            if (Set(ref _mergeRequest, value)) OnPropertiesChanged(nameof(HasMergeRequest), nameof(MrKind), nameof(MrBadge));
        }
    }

    public ForgePipeline? Pipeline
    {
        get => _pipeline;
        private set
        {
            if (Set(ref _pipeline, value)) OnPropertiesChanged(nameof(HasPipeline), nameof(PipelineKind), nameof(PipelineLabel));
        }
    }

    public string? ServerError { get => _serverError; private set => Set(ref _serverError, value); }
    public DateTime? ServerRefreshed { get => _serverRefreshed; private set => Set(ref _serverRefreshed, value); }

    public bool HasMergeRequest => MergeRequest is not null;
    public bool HasPipeline => Pipeline is not null;
    public string PipelineKind => Pipeline?.Kind ?? "none";
    public string PipelineLabel => Pipeline is null ? "no checks" : $"{(IsGitHubServer ? "checks" : "pipeline")} {Pipeline.Label}";
    public string MrBadge => MergeRequest is null ? string.Empty : $"{MergeRequest.Reference} {MergeRequest.Readiness}";

    /// <summary>merged, closed, conflicts, draft or open - for the badge color.</summary>
    public string MrKind => MergeRequest switch
    {
        null => "none",
        { State: "merged" } => "merged",
        { State: "closed" } => "closed",
        { HasConflicts: true } => "conflicts",
        { Draft: true } => "draft",
        { DetailedMergeStatus: "mergeable" } => "ready",
        _ => "open",
    };

    // ------------------------------------------------------------------ busy

    private bool _isBusy;
    private string _busyText = string.Empty;

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }

    private CancellationTokenSource? _work;

    /// <summary>A token for the action that's starting; <see cref="CancelWork"/> stops it (git is killed).</summary>
    public CancellationToken BeginWork()
    {
        _work?.Dispose();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    public void EndWork()
    {
        _work?.Dispose();
        _work = null;
    }

    public void CancelWork() => _work?.Cancel();

    private UndoPoint? _lastUndo;

    /// <summary>The refs before the app's last action that moved a branch here; Undo puts them back.</summary>
    public UndoPoint? LastUndo { get => _lastUndo; set { if (Set(ref _lastUndo, value)) OnPropertyChanged(nameof(UndoText)); } }

    public string UndoText => LastUndo is null ? "Nothing to undo" : $"Undo {LastUndo.Title.ToLowerInvariant()}";

    // ------------------------------------------------------------------ derived

    public RepoState State
    {
        get
        {
            if (!Loaded) return RepoState.Unknown;
            if (_missing) return RepoState.Missing;
            if (Error is not null) return RepoState.Error;
            if (ConflictCount > 0 || InProgress) return RepoState.Conflict;
            if (Ahead > 0 && Behind > 0) return RepoState.Diverged;
            if (Behind > 0) return RepoState.Behind;
            if (IsDirty) return RepoState.Dirty;
            if (Ahead > 0) return RepoState.Ahead;
            if (!HasUpstream && !Detached && HasForge && !OnDefaultBranch) return RepoState.Unpublished;
            return RepoState.Clean;
        }
    }

    public string StateLabel => State switch
    {
        RepoState.Unknown => "loading...",
        RepoState.Missing => "folder not found",
        RepoState.Error => "error",
        RepoState.Conflict => ConflictCount > 0 ? $"{ConflictCount} conflict(s)" : InProgressText.ToLowerInvariant(),
        RepoState.Diverged => $"diverged ({Ahead} / {Behind})",
        RepoState.Behind => $"{Behind} to pull",
        RepoState.Dirty => $"{ChangeCount} change(s)",
        RepoState.Ahead => $"{Ahead} to push",
        RepoState.Unpublished => "not published",
        _ => "up to date",
    };

    /// <summary>Something needs you: conflicts, errors, the server moved on, a failed pipeline, a finished MR,
    /// or commits would go out under the wrong identity.</summary>
    public bool NeedsAttention =>
        State is RepoState.Missing or RepoState.Error or RepoState.Conflict or RepoState.Diverged or RepoState.Behind
        || PipelineKind == "failed"
        || MrKind is "conflicts" or "merged"
        || (IdentityMismatch && (IsDirty || Ahead > 0))
        || (IsFeatureBranch && BehindDefault > 0 && HasMergeRequest && MergeRequest!.State == "opened");

    public NextStep Next
    {
        get
        {
            if (!Loaded) return new NextStep("Loading...", "Reading the repo's status.", "", "", "info");
            if (_missing)
                return new NextStep("Folder not found", $"{Path} doesn't exist any more. Remove it from the list, or restore the folder.", "remove", "Remove from list", "error");
            if (Error is not null) return new NextStep("Git reported an error", Error, "refresh", "Refresh", "error");
            if (InProgress && ConflictCount > 0)
                return new NextStep($"Resolve {ConflictCount} conflicted file(s)",
                    $"{InProgressText}. For each file choose Keep mine or Take theirs (or edit it), then Continue.", "changes", "Resolve conflicts", "error");
            if (InProgress)
                return new NextStep($"{InProgressText} - ready to finish", "All conflicts are resolved. Continue completes it.", "continue", "Continue", "warning");
            if (IdentityMismatch && (IsDirty || Ahead > 0))
                return new NextStep($"Use your {WorkspaceName} identity here",
                    IdentityProblem + " Fix it before you commit or push.", "fix-identity", $"Use {Workspace.GitEmail}", "error");
            if (Ahead > 0 && Behind > 0)
                return new NextStep("Your branch and the server's have both moved",
                    $"{Behind} new commit(s) on the server, {Ahead} of yours. Pull puts yours on top of theirs, then push.", "sync", "Pull & push", "warning");
            if (Behind > 0)
                return new NextStep($"Pull {Behind} new commit(s)", $"{Upstream} has commits you don't have yet.", "pull", "Pull", "warning");
            if (MrKind == "merged" && IsFeatureBranch)
                return new NextStep($"{MergeRequest!.Reference} was merged - finish the branch",
                    $"Switch to {DefaultBranch}, update it, and delete '{Branch}' locally and on the server.", "finish", "Finish branch", "success");
            if (IsDirty)
                return new NextStep($"Commit {ChangeCount} change(s)",
                    "Review the diff, write a message, and commit (Commit & Push sends it too).", "changes", "Review & commit", "info");
            if (IsFeatureBranch && HasForge && !HasUpstream)
                return new NextStep("Publish this branch",
                    $"The branch only exists on this PC. Push it so {ServerName} (and a {MrTerm}) can see it.", "push", "Publish", "info");
            if (Ahead > 0)
                return new NextStep($"Push {Ahead} commit(s)", $"Send your commits to {Upstream}.", "push", "Push", "info");
            if (PipelineKind == "failed")
                return new NextStep($"The {PipelineTerm} failed", "See which job failed and read its log, or retry it.", "pipelines", "Show failure", "error");
            if (MrKind == "conflicts" || (IsFeatureBranch && BehindDefault > 0 && HasMergeRequest && MergeRequest!.State == "opened"))
                return new NextStep($"{DefaultBranch} has {BehindDefault} new commit(s)",
                    $"Bring {DefaultRef} into '{Branch}' so the {MrTerm} stays mergeable.", "update", "Update from " + DefaultBranch, "warning");
            if (IsFeatureBranch && HasForge && !HasMergeRequest && AheadOfDefault > 0 && HasToken)
                return new NextStep($"Open a {MrTerm}",
                    $"'{Branch}' has {AheadOfDefault} commit(s) that aren't in {DefaultBranch}.", "create-mr", $"Create {MrTerm}", "info");
            if (MrKind == "ready")
                return new NextStep($"{MergeRequest!.Reference} is ready to merge", MergeRequest.Title, "mrs", $"Open {MrTerm}", "success");
            if (PipelineKind == "running" || PipelineKind == "pending")
                return new NextStep($"{(IsGitHubServer ? "Checks" : "Pipeline")} running", "Nothing to do - you'll get a notification when it finishes.", "pipelines", "Watch", "info");
            if (OnDefaultBranch)
                return new NextStep($"Up to date on {Branch}", "Start a feature branch for your next change.", "start-branch", "Start a branch", "success");
            return new NextStep("All good", "Nothing to do here right now.", "", "", "success");
        }
    }

    private void RaiseDerived() => OnPropertiesChanged(
        nameof(State), nameof(StateLabel), nameof(StatusGroup), nameof(NeedsAttention), nameof(Next), nameof(ChangeCount), nameof(IsDirty),
        nameof(OnDefaultBranch), nameof(IsFeatureBranch), nameof(HasUpstream), nameof(HasForge), nameof(HasToken),
        nameof(LastFetchText), nameof(HeadDateText), nameof(DefaultRef), nameof(InProgress), nameof(InProgressText), nameof(TrackingText),
        nameof(PullText), nameof(PushText), nameof(UpdateText), nameof(IdentityMismatch), nameof(IdentityText), nameof(IdentityProblem),
        nameof(NewOnDefaultText), nameof(UpdateFromDefaultHint));

    /// <summary>Called after the token store changes.</summary>
    public void RaiseTokenChanged() => RaiseDerived();

    // ------------------------------------------------------------------ refresh

    /// <summary>Re-reads everything git knows locally (no network, except asking the server for its default
    /// branch the very first time).</summary>
    public async Task RefreshLocalAsync(CancellationToken ct = default)
    {
        try
        {
            _missing = !Directory.Exists(Path) || !Git.IsRepo(Path);
            if (_missing)
            {
                Error = null;
                return;
            }

            var status = await Git.GetStatusAsync(Path, ct);
            if (!status.Ok)
            {
                Error = status.Error;
                return;
            }
            Error = null;
            Branch = status.Branch;
            Detached = status.Detached;
            Upstream = status.Upstream;
            UpstreamGone = status.UpstreamGone;
            Ahead = status.Ahead;
            Behind = status.Behind;
            StagedCount = status.Staged.Count;
            UnstagedCount = status.Unstaged.Count;
            UntrackedCount = status.Unstaged.Count(c => c.IsUntracked);
            ConflictCount = status.Conflicted.Count;
            MergeInProgress = Git.IsMergeInProgress(Path);
            RebaseInProgress = Git.IsRebaseInProgress(Path);
            LastFetch = Git.LastFetch(Path);
            (GitName, GitEmail, HasLocalIdentity) = await Git.GetIdentityAsync(Path, ct);

            if (RemoteName is null)
            {
                RemoteName = await Git.GetRemoteNameAsync(Path, ct);
                if (RemoteName is not null) Remote = ForgeLinks.ParseRemote(await Git.GetRemoteUrlAsync(Path, RemoteName, ct));
            }
            if (RemoteName is not null)
            {
                DefaultBranch ??= await Git.GetDefaultBranchAsync(Path, RemoteName, ct);
                var vsDefault = !Detached && Branch != DefaultBranch ? await Git.AheadBehindAsync(Path, $"{RemoteName}/{DefaultBranch}", ct) : null;
                AheadOfDefault = vsDefault?.Ahead ?? 0;
                BehindDefault = vsDefault?.Behind ?? 0;
            }

            var head = await Git.GetHeadCommitAsync(Path, ct);
            HeadSubject = head?.Subject ?? "(no commits yet)";
            HeadDate = head?.Date;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loaded = true;
            IsStale = false;
            RaiseDerived();
        }
    }

    /// <summary>The branch's merge request / pull request and latest pipeline / checks from the server. Returns
    /// notification texts for changes worth a tray balloon (a pipeline finishing, a merge request being merged).</summary>
    public async Task<List<(string Title, string Text)>> RefreshServerAsync(CancellationToken ct = default)
    {
        var notes = new List<(string, string)>();
        if (Remote is null || Detached || Branch.Length == 0 || Forges.For(Remote) is not { } client)
        {
            MergeRequest = null;
            Pipeline = null;
            RaiseDerived();
            return notes;
        }

        try
        {
            var oldPipeline = Pipeline;
            var oldMr = MergeRequest;

            var mrTask = OnDefaultBranch ? Task.FromResult<ForgeMergeRequest?>(null) : client.GetBranchMergeRequestAsync(Remote, Branch, ct);
            var pipeTask = client.GetLatestPipelineAsync(Remote, Branch, ct);
            var mr = await mrTask;
            ForgePipeline? pipe;
            try
            {
                pipe = await pipeTask;
            }
            catch (UserFacingException) when (IsGitHubServer)
            {
                pipe = null; // Actions disabled for the repository
            }
            MergeRequest = mr;
            Pipeline = pipe;
            ServerError = null;
            ServerRefreshed = DateTime.Now;

            if (oldPipeline is not null && pipe is not null && oldPipeline.Id == pipe.Id && oldPipeline.Kind != pipe.Kind
                && pipe.Kind is "success" or "failed" or "canceled")
            {
                notes.Add(($"{Name}: {PipelineTerm} {pipe.Label}", $"#{pipe.Id} on {Branch}"));
            }
            if (oldMr is not null && mr is not null && oldMr.Iid == mr.Iid && oldMr.State != "merged" && mr.State == "merged")
            {
                notes.Add(($"{Name}: {mr.Reference} merged", $"{mr.Title}\nFinish the branch to clean up."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ServerError = ex.Message;
        }
        finally
        {
            RaiseDerived();
        }
        return notes;
    }

    // ------------------------------------------------------------------ status cache / change feed

    /// <summary>What the repo knows right now, for status-cache.json and the change feed's before / after.</summary>
    public RepoSnapshot ToSnapshot() => new(Path, Branch, Detached, Upstream, Ahead, Behind, StagedCount, UnstagedCount, UntrackedCount,
        ConflictCount, DefaultBranch, HeadSubject, HeadDate, LastFetch, Pipeline, MergeRequest, DateTime.Now);

    /// <summary>Shows a cached snapshot at start-up (marked stale) until <see cref="RefreshLocalAsync"/> replaces it.
    /// Only before the first real refresh; the remote is parsed from .git/config (cheap, no process).</summary>
    public void ApplySnapshot(RepoSnapshot s)
    {
        if (Loaded) return;
        _missing = false;
        Branch = s.Branch;
        Detached = s.Detached;
        Upstream = s.Upstream;
        Ahead = s.Ahead;
        Behind = s.Behind;
        StagedCount = s.Staged;
        UnstagedCount = s.Unstaged;
        UntrackedCount = s.Untracked;
        ConflictCount = s.Conflicts;
        DefaultBranch ??= s.DefaultBranch;
        HeadSubject = s.HeadSubject;
        HeadDate = s.HeadDate;
        LastFetch = s.LastFetch;
        MergeRequest = s.MergeRequest;
        Pipeline = s.Pipeline;
        _cachedAt = s.SavedAt;
        IsStale = true;
        Loaded = true;
        RaiseDerived();
    }

    /// <summary>Forget cached remote facts (after a clone / remote change / default branch change).</summary>
    public void ResetRemoteCache()
    {
        RemoteName = null;
        DefaultBranch = null;
        Remote = null;
    }
}
