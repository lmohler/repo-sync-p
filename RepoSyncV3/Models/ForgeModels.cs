namespace RepoSyncV3.Models;

// The shapes the app works with for both servers. GitLab's REST API (v4) responses deserialize straight into them
// (snake_case JSON through JsonNamingPolicy.SnakeCaseLower, see GitLabClient); GitHub's are mapped field by field
// in GitHubClient. Fields a server leaves out just stay null.

/// <summary>The kind of server a repo's remote points at.</summary>
public enum ForgeKind
{
    GitLab,
    GitHub,
}

public sealed record ForgeUser(long Id, string Username, string Name);

/// <summary>A GitLab merge request or a GitHub pull request. <see cref="State"/> is GitLab's vocabulary
/// (opened, merged, closed) and <see cref="DetailedMergeStatus"/> GitLab's detailed_merge_status; GitHub's
/// values are translated into them.</summary>
public sealed record ForgeMergeRequest(
    long Id, int Iid, string Title, string State, bool Draft, string WebUrl, bool HasConflicts,
    string SourceBranch, string TargetBranch, string? DetailedMergeStatus, string? Description,
    ForgeUser? Author, List<ForgeUser>? Reviewers, DateTime? UpdatedAt, DateTime? MergedAt,
    int? UserNotesCount, ForgePipeline? HeadPipeline)
{
    /// <summary>"!" for GitLab merge requests, "#" for GitHub pull requests.</summary>
    public string Prefix { get; init; } = "!";

    /// <summary>GitHub's GraphQL id (draft / ready needs it); null for GitLab.</summary>
    public string? NodeId { get; init; }

    /// <summary>The repo it was listed for (cross-repo lists on the home page).</summary>
    public string? RepoName { get; init; }

    public string Reference => $"{Prefix}{Iid}";

    public string AuthorName => Author?.Name ?? string.Empty;

    public string Branches => $"{SourceBranch} -> {TargetBranch}";

    /// <summary>merged, closed, conflicts, draft, ready or open - for the badge color.</summary>
    public string Kind => State switch
    {
        "merged" => "merged",
        "closed" => "closed",
        _ when HasConflicts => "conflicts",
        _ when Draft => "draft",
        _ when DetailedMergeStatus == "mergeable" => "ready",
        _ => "open",
    };

    public string PipelineKind => HeadPipeline?.Kind ?? "none";
    public string PipelineText => HeadPipeline is null ? "no pipeline" : $"checks {HeadPipeline.Label}";

    /// <summary>One word for the grid / badges: draft, conflicts, ready, checking, blocked, merged, closed.</summary>
    public string Readiness => State switch
    {
        "merged" => "merged",
        "closed" => "closed",
        _ when HasConflicts => "conflicts",
        _ when Draft => "draft",
        _ => DetailedMergeStatus switch
        {
            "mergeable" => "ready",
            "unstable" => "checks failing",
            "checking" or "unchecked" or "preparing" => "checking",
            "ci_still_running" or "ci_must_pass" => "pipeline",
            "not_approved" => "needs approval",
            "discussions_not_resolved" => "open threads",
            "need_rebase" => "needs rebase",
            null => "open",
            _ => "blocked",
        },
    };

    public string MergeStatusText => DetailedMergeStatus switch
    {
        "mergeable" => "Ready to merge",
        "unstable" => "Mergeable, but some checks failed",
        "ci_must_pass" => "Waiting for the pipeline to pass",
        "ci_still_running" => "Pipeline still running",
        "not_approved" => "Needs approval",
        "discussions_not_resolved" => "Unresolved threads",
        "draft_status" => "Marked as draft",
        "conflict" => "Has merge conflicts",
        "need_rebase" => "Needs a rebase / update from the target branch",
        "blocked_status" => "Blocked by another merge request",
        "protected" => "Blocked by branch protection (required reviews or checks)",
        "checking" or "unchecked" or "preparing" => "The server is checking mergeability",
        "not_open" => State,
        null => State,
        _ => DetailedMergeStatus.Replace('_', ' '),
    };
}

/// <summary>A GitLab pipeline or a GitHub Actions workflow run.</summary>
public sealed record ForgePipeline(
    long Id, string Status, string WebUrl, string? Ref, string? Sha, string? Source, DateTime? CreatedAt, DateTime? UpdatedAt,
    string? Name = null)
{
    public string ShortSha => Sha is { Length: > 8 } ? Sha[..8] : Sha ?? string.Empty;

    /// <summary>The server's status reduced to the few the UI colors: success, failed, running, pending, canceled, skipped, manual.</summary>
    public string Kind => ForgeStatus.Kind(Status);

    public string Label => ForgeStatus.Label(Status);
}

public sealed record ForgeJob(
    long Id, string Name, string Stage, string Status, string WebUrl, double? Duration, DateTime? StartedAt, bool AllowFailure)
{
    public string Kind => ForgeStatus.Kind(Status);

    public string Label => AllowFailure && Status == "failed" ? "failed (allowed)" : ForgeStatus.Label(Status);

    public string DurationText => Duration is { } d
        ? d >= 60 ? $"{(int)(d / 60)}m {(int)(d % 60)}s" : $"{d:0}s"
        : string.Empty;
}

public sealed record ForgeProject(
    long Id, string PathWithNamespace, string? Description, string HttpUrlToRepo, string SshUrlToRepo,
    string WebUrl, string? DefaultBranch, DateTime? LastActivityAt);

/// <summary>A git remote that points at a GitLab project or a GitHub repository: the kind of server, its web
/// address (https, with a port when the remote URL has one) and the project's full path, e.g. "group/project".</summary>
/// <summary>A merge / pull request comment. Path and Line are set for comments on a diff line.</summary>
public sealed record ForgeComment(string Author, string Body, DateTime? Created, string? Path, int? Line)
{
    public string Where => Path is null ? string.Empty : Line is { } l ? $"{Path}:{l}" : Path;
}

public sealed record ForgeRemote(ForgeKind Kind, string Host, string BaseUrl, string ProjectPath)
{
    public string ApiBase => Kind == ForgeKind.GitHub ? "https://api.github.com" : $"{BaseUrl}/api/v4";
    public string WebUrl => $"{BaseUrl}/{ProjectPath}";

    /// <summary>GitLab's API id for the project: its path, URL-encoded ("group%2Fproject").</summary>
    public string ApiId => Uri.EscapeDataString(ProjectPath);

    public bool IsGitHub => Kind == ForgeKind.GitHub;
    public string ServerName => IsGitHub ? "GitHub" : "GitLab";
}

/// <summary>Which merge requests / pull requests to list.</summary>
public enum MergeRequestScope
{
    AllOpen,
    Mine,
    ToReview,
    Merged,
}

public static class ForgeStatus
{
    public static string Kind(string? status) => status switch
    {
        "success" => "success",
        "failed" => "failed",
        "running" => "running",
        "canceled" or "canceling" => "canceled",
        "skipped" => "skipped",
        "manual" => "manual",
        null or "" => "none",
        _ => "pending", // created, waiting_for_resource, preparing, pending, scheduled
    };

    public static string Label(string? status) => status switch
    {
        "success" => "passed",
        "waiting_for_resource" or "preparing" or "scheduled" or "created" => "pending",
        null or "" => "none",
        _ => status,
    };

    /// <summary>GitHub's status + conclusion as one GitLab-style status.</summary>
    public static string FromGitHub(string? status, string? conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => "success",
            "failure" or "timed_out" or "startup_failure" => "failed",
            "cancelled" => "canceled",
            "skipped" or "neutral" or "stale" => "skipped",
            "action_required" => "manual",
            _ => "failed",
        },
        "in_progress" => "running",
        _ => "pending", // queued, waiting, requested, pending
    };
}
