using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// Calls GitHub's REST API (and GraphQL for draft / ready) with a personal access token. Pull requests and
/// Actions workflow runs are translated into the same shapes as GitLab's merge requests and pipelines, so the rest
/// of the app doesn't care which server a repo is on.
/// </summary>
public sealed class GitHubClient : IForge
{
    private const string Api = "https://api.github.com";

    // one /user lookup per token per session
    private static readonly Dictionary<string, ForgeUser> UserCache = new();

    private readonly string _token;

    public GitHubClient(string token)
    {
        _token = token;
    }

    public ForgeKind Kind => ForgeKind.GitHub;

    private static string Repo(ForgeRemote r) => r.ProjectPath;
    private static string Owner(ForgeRemote r) => r.ProjectPath.Split('/')[0];

    // ---- user ----

    public async Task<ForgeUser> GetCurrentUserUncachedAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("user", ct);
        var u = doc.RootElement;
        var login = Str(u, "login") ?? string.Empty;
        return new ForgeUser(Long(u, "id"), login, Str(u, "name") ?? login);
    }

    public async Task<ForgeUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        lock (UserCache)
        {
            if (UserCache.TryGetValue(_token, out var cached)) return cached;
        }
        var user = await GetCurrentUserUncachedAsync(ct);
        lock (UserCache) UserCache[_token] = user;
        return user;
    }

    // ---- pull requests ----

    public async Task<ForgeMergeRequest?> GetBranchMergeRequestAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        using var doc = await GetAsync(
            $"repos/{Repo(remote)}/pulls?head={Uri.EscapeDataString(Owner(remote) + ":" + branch)}&state=all&sort=updated&direction=desc&per_page=5", ct);
        var list = doc.RootElement.EnumerateArray().Select(MapPull).ToList();
        return list.FirstOrDefault(m => m.State == "opened") ?? list.FirstOrDefault();
    }

    /// <summary>One pull request with its mergeability and the checks on its head commit.</summary>
    public async Task<ForgeMergeRequest> GetMergeRequestAsync(ForgeRemote remote, int iid, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"repos/{Repo(remote)}/pulls/{iid}", ct);
        var mr = MapPull(doc.RootElement);
        var sha = doc.RootElement.TryGetProperty("head", out var head) ? Str(head, "sha") : null;
        if (sha is null) return mr;
        try
        {
            using var runs = await GetAsync($"repos/{Repo(remote)}/actions/runs?head_sha={sha}&per_page=20", ct);
            return mr with { HeadPipeline = Aggregate(RunsOf(runs.RootElement)) };
        }
        catch (UserFacingException)
        {
            return mr; // Actions may be off for the repo
        }
    }

    public async Task<List<ForgeMergeRequest>> ListMergeRequestsAsync(ForgeRemote remote, MergeRequestScope scope, CancellationToken ct = default)
    {
        var state = scope == MergeRequestScope.Merged ? "closed" : "open";
        using var doc = await GetAsync($"repos/{Repo(remote)}/pulls?state={state}&sort=updated&direction=desc&per_page=50", ct);
        var pulls = doc.RootElement.EnumerateArray().ToList();
        switch (scope)
        {
            case MergeRequestScope.Mine:
            {
                var me = (await GetCurrentUserAsync(ct)).Username;
                pulls = pulls.Where(p => p.TryGetProperty("user", out var u) && string.Equals(Str(u, "login"), me, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
            }
            case MergeRequestScope.ToReview:
            {
                var me = (await GetCurrentUserAsync(ct)).Username;
                pulls = pulls.Where(p => p.TryGetProperty("requested_reviewers", out var rs) && rs.ValueKind == JsonValueKind.Array
                                         && rs.EnumerateArray().Any(r => string.Equals(Str(r, "login"), me, StringComparison.OrdinalIgnoreCase))).ToList();
                break;
            }
            case MergeRequestScope.Merged:
                pulls = pulls.Where(p => Str(p, "merged_at") is not null).ToList();
                break;
        }
        return pulls.Select(MapPull).ToList();
    }

    /// <summary>Squash and branch deletion are chosen when merging on GitHub, so they are ignored here.</summary>
    public async Task<ForgeMergeRequest> CreateMergeRequestAsync(ForgeRemote remote, string sourceBranch, string targetBranch, string title,
        string description, bool draft, bool removeSourceBranch, bool squash, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/pulls", new
        {
            title,
            head = sourceBranch,
            @base = targetBranch,
            body = description,
            draft,
        }, ct);
        return MapPull(doc.RootElement);
    }

    public async Task MergeAsync(ForgeRemote remote, ForgeMergeRequest mr, bool squash, bool removeSourceBranch, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Put, $"repos/{Repo(remote)}/pulls/{mr.Iid}/merge", new { merge_method = squash ? "squash" : "merge" }, ct))
        {
        }
        if (!removeSourceBranch) return;
        try
        {
            var refPath = string.Join("/", mr.SourceBranch.Split('/').Select(Uri.EscapeDataString));
            using (await SendAsync(HttpMethod.Delete, $"repos/{Repo(remote)}/git/refs/heads/{refPath}", null, ct))
            {
            }
        }
        catch (UserFacingException)
        {
            // already deleted (the repo may delete merged branches by itself)
        }
    }

    public async Task ApproveAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/pulls/{mr.Iid}/reviews", new { @event = "APPROVE" }, ct))
        {
        }
    }

    /// <summary>The REST API can't change draft status; GraphQL can.</summary>
    public async Task SetDraftAsync(ForgeRemote remote, ForgeMergeRequest mr, bool draft, CancellationToken ct = default)
    {
        var nodeId = mr.NodeId;
        if (nodeId is null)
        {
            using var doc = await GetAsync($"repos/{Repo(remote)}/pulls/{mr.Iid}", ct);
            nodeId = Str(doc.RootElement, "node_id") ?? throw new UserFacingException("GitHub didn't return the pull request's id.");
        }
        var mutation = draft
            ? "mutation($id:ID!){convertPullRequestToDraft(input:{pullRequestId:$id}){clientMutationId}}"
            : "mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){clientMutationId}}";
        using var result = await SendAsync(HttpMethod.Post, "graphql", new { query = mutation, variables = new { id = nodeId } }, ct);
        if (result.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new UserFacingException("GitHub could not change the draft status: " + string.Join("; ", errors.EnumerateArray().Select(e => Str(e, "message"))));
    }

    // ---- workflow runs (pipelines) ----

    /// <summary>The newest commit on the branch that has runs, as one status: the worst of its workflows
    /// (failed beats running beats passed), so a failing lint workflow isn't hidden behind a passing build.</summary>
    public async Task<ForgePipeline?> GetLatestPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"repos/{Repo(remote)}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=20", ct);
        var runs = RunsOf(doc.RootElement);
        if (runs.Count == 0) return null;
        return Aggregate(runs.Where(r => r.Sha == runs[0].Sha).ToList());
    }

    public async Task<List<ForgePipeline>> ListPipelinesAsync(ForgeRemote remote, string? branch, CancellationToken ct = default)
    {
        using var doc = await GetAsync(
            $"repos/{Repo(remote)}/actions/runs?per_page=30{(branch is null ? "" : "&branch=" + Uri.EscapeDataString(branch))}", ct);
        return RunsOf(doc.RootElement);
    }

    public async Task<List<ForgeJob>> ListJobsAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"repos/{Repo(remote)}/actions/runs/{pipelineId}/jobs?per_page=100", ct);
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs)) return new List<ForgeJob>();
        return jobs.EnumerateArray().Select(j =>
        {
            var started = Date(j, "started_at");
            var completed = Date(j, "completed_at");
            double? duration = started is { } s ? ((completed ?? DateTime.UtcNow) - s).TotalSeconds : null;
            return new ForgeJob(Long(j, "id"), Str(j, "name") ?? "job", Str(j, "workflow_name") ?? string.Empty,
                ForgeStatus.FromGitHub(Str(j, "status"), Str(j, "conclusion")), Str(j, "html_url") ?? string.Empty, duration, started, false);
        }).ToList();
    }

    public async Task RetryPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/actions/runs/{pipelineId}/rerun-failed-jobs", null, ct))
        {
        }
    }

    public async Task CancelPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/actions/runs/{pipelineId}/cancel", null, ct))
        {
        }
    }

    /// <summary>Starts every active workflow that can be started by hand (has a workflow_dispatch trigger).</summary>
    public async Task<string> RunPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"repos/{Repo(remote)}/actions/workflows?per_page=100", ct);
        var workflows = doc.RootElement.TryGetProperty("workflows", out var w)
            ? w.EnumerateArray().Where(x => Str(x, "state") == "active").Select(x => (Id: Long(x, "id"), Name: Str(x, "name") ?? "workflow")).ToList()
            : new List<(long Id, string Name)>();
        if (workflows.Count == 0) throw new UserFacingException("This repository has no active GitHub Actions workflows.");

        var started = new List<string>();
        foreach (var (id, name) in workflows)
        {
            try
            {
                using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/actions/workflows/{id}/dispatches", new { @ref = branch }, ct))
                {
                }
                started.Add(name);
            }
            catch (UserFacingException)
            {
                // no workflow_dispatch trigger - can't be started by hand
            }
        }
        if (started.Count == 0)
            throw new UserFacingException("None of this repository's workflows can be started by hand.",
                "Add 'workflow_dispatch:' under 'on:' in a workflow file to allow it, or push a commit to trigger the workflows.");
        return "Started " + string.Join(", ", started);
    }

    public async Task RetryJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/actions/jobs/{jobId}/rerun", null, ct))
        {
        }
    }

    public Task PlayJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default) =>
        throw new UserFacingException("GitHub Actions has no manual jobs.", "Use Run workflows to start a workflow that has a workflow_dispatch trigger.");

    /// <summary>The job log (GitHub redirects to a short-lived download link; the token is not sent there).
    /// Timestamps and ANSI colors are removed.</summary>
    public async Task<string> GetJobLogAsync(ForgeRemote remote, long jobId, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Get, $"repos/{Repo(remote)}/actions/jobs/{jobId}/logs", null, ct);
        var text = Regex.Replace(raw, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        text = Regex.Replace(text, @"(?m)^﻿?\d{4}-\d\d-\d\dT[\d:.]+Z ", string.Empty);
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    // ---- comments / artifacts ----

    /// <summary>Conversation comments plus review comments on diff lines, merged by time.</summary>
    public async Task<List<ForgeComment>> ListCommentsAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default)
    {
        var list = new List<ForgeComment>();
        using (var doc = await GetAsync($"repos/{Repo(remote)}/issues/{mr.Iid}/comments?per_page=100", ct))
            foreach (var c in doc.RootElement.EnumerateArray())
                list.Add(new ForgeComment(c.TryGetProperty("user", out var u) ? Str(u, "login") ?? "?" : "?", Str(c, "body") ?? "", Date(c, "created_at")?.ToLocalTime(), null, null));
        using (var doc = await GetAsync($"repos/{Repo(remote)}/pulls/{mr.Iid}/comments?per_page=100", ct))
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                int? line = c.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
                list.Add(new ForgeComment(c.TryGetProperty("user", out var u) ? Str(u, "login") ?? "?" : "?", Str(c, "body") ?? "", Date(c, "created_at")?.ToLocalTime(), Str(c, "path"), line));
            }
        return list.OrderBy(c => c.Created).ToList();
    }

    public async Task AddCommentAsync(ForgeRemote remote, ForgeMergeRequest mr, string body, CancellationToken ct = default)
    {
        using (await SendAsync(HttpMethod.Post, $"repos/{Repo(remote)}/issues/{mr.Iid}/comments", new { body }, ct))
        {
        }
    }

    public async Task<List<string>> DownloadArtifactsAsync(ForgeRemote remote, long pipelineId, long? jobId, string folder, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"repos/{Repo(remote)}/actions/runs/{pipelineId}/artifacts?per_page=100", ct);
        var artifacts = doc.RootElement.TryGetProperty("artifacts", out var a)
            ? a.EnumerateArray().Where(x => !(x.TryGetProperty("expired", out var e) && e.ValueKind == JsonValueKind.True))
                .Select(x => (Name: Str(x, "name") ?? "artifact", Url: Str(x, "archive_download_url"))).Where(x => x.Url is not null).ToList()
            : new();
        if (artifacts.Count == 0) throw new UserFacingException("This run has no artifacts (or they expired).");
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        foreach (var (name, url) in artifacts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.UserAgent.ParseAdd("repo-sync-v3");
            using var response = await Forges.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UserFacingException($"Could not download artifact '{name}' ({(int)response.StatusCode}).");
            var file = Path.Combine(folder, $"{string.Concat(name.Split(Path.GetInvalidFileNameChars()))}.zip");
            await using (var fs = File.Create(file)) await response.Content.CopyToAsync(fs, ct);
            files.Add(file);
        }
        return files;
    }

    // ---- repositories ----

    public async Task<List<ForgeProject>> ListMyProjectsAsync(int max = 500, CancellationToken ct = default)
    {
        var all = new List<ForgeProject>();
        for (var page = 1; all.Count < max; page++)
        {
            using var doc = await GetAsync($"user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&per_page=100&page={page}", ct);
            var batch = doc.RootElement.EnumerateArray().Where(r => !Bool(r, "archived"))
                .Select(r => new ForgeProject(Long(r, "id"), Str(r, "full_name") ?? string.Empty, Str(r, "description"),
                    Str(r, "clone_url") ?? string.Empty, Str(r, "ssh_url") ?? string.Empty, Str(r, "html_url") ?? string.Empty,
                    Str(r, "default_branch"), Date(r, "pushed_at")))
                .ToList();
            all.AddRange(batch);
            if (doc.RootElement.GetArrayLength() < 100) break;
        }
        return all;
    }

    // ---- mapping ----

    private static ForgeMergeRequest MapPull(JsonElement p)
    {
        var merged = Str(p, "merged_at") is not null;
        var state = Str(p, "state") == "open" ? "opened" : merged ? "merged" : "closed";
        var mergeableState = Str(p, "mergeable_state");
        var detailed = mergeableState switch
        {
            "clean" or "has_hooks" => "mergeable",
            "unstable" => "unstable",
            "dirty" => "conflict",
            "behind" => "need_rebase",
            "blocked" => "protected",
            "draft" => "draft_status",
            "unknown" => "checking",
            _ => null,
        };
        ForgeUser? author = p.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            ? new ForgeUser(Long(u, "id"), Str(u, "login") ?? string.Empty, Str(u, "login") ?? string.Empty)
            : null;
        var reviewers = p.TryGetProperty("requested_reviewers", out var rs) && rs.ValueKind == JsonValueKind.Array
            ? rs.EnumerateArray().Select(r => new ForgeUser(Long(r, "id"), Str(r, "login") ?? string.Empty, Str(r, "login") ?? string.Empty)).ToList()
            : null;
        int? comments = p.TryGetProperty("comments", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() + (p.TryGetProperty("review_comments", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : 0)
            : null;
        return new ForgeMergeRequest(
            Long(p, "id"), (int)Long(p, "number"), Str(p, "title") ?? string.Empty, state, Bool(p, "draft"), Str(p, "html_url") ?? string.Empty,
            mergeableState == "dirty",
            p.TryGetProperty("head", out var head) ? Str(head, "ref") ?? string.Empty : string.Empty,
            p.TryGetProperty("base", out var bse) ? Str(bse, "ref") ?? string.Empty : string.Empty,
            state == "opened" ? detailed : "not_open", Str(p, "body"), author, reviewers, Date(p, "updated_at"), Date(p, "merged_at"),
            comments, null)
        {
            Prefix = "#",
            NodeId = Str(p, "node_id"),
        };
    }

    private static List<ForgePipeline> RunsOf(JsonElement root) =>
        root.TryGetProperty("workflow_runs", out var runs)
            ? runs.EnumerateArray().Select(r => new ForgePipeline(
                Long(r, "id"), ForgeStatus.FromGitHub(Str(r, "status"), Str(r, "conclusion")), Str(r, "html_url") ?? string.Empty,
                Str(r, "head_branch"), Str(r, "head_sha"), Str(r, "event"), Date(r, "created_at"), Date(r, "updated_at"), Str(r, "name"))).ToList()
            : new List<ForgePipeline>();

    private static readonly string[] Severity = { "failed", "running", "pending", "manual", "canceled", "success", "skipped" };

    /// <summary>Several workflow runs on one commit as one: the worst status wins (its run id is kept, so its
    /// jobs are the ones shown).</summary>
    private static ForgePipeline? Aggregate(List<ForgePipeline> runs)
    {
        if (runs.Count == 0) return null;
        var worst = runs.OrderBy(r => Array.IndexOf(Severity, r.Kind) is var i && i < 0 ? 99 : i).First();
        return runs.Count == 1 ? worst : worst with { Name = $"{worst.Name} (+{runs.Count - 1} more)" };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var d) ? d : null;

    // ---- plumbing ----

    private Task<JsonDocument> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var text = await SendRawAsync(method, path, body, ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>
    /// Last answer per GET url. GitHub answers If-None-Match with 304 when nothing changed, and 304s don't count
    /// against the 5,000 calls / hour, so the background refresh of many repos stays well inside the limit.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string ETag, string Body)> EtagCache = new();
    private const int EtagCacheMax = 2000;

    /// <summary>Calls left / reset time from the last response. Below the floor, reads wait for the reset.</summary>
    private static int _remaining = int.MaxValue;
    private static DateTimeOffset _resetAt = DateTimeOffset.MinValue;
    private const int RemainingFloor = 25;

    private async Task<string> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = $"{Api}/{path}";
        var isGet = method == HttpMethod.Get;
        if (isGet && _remaining < RemainingFloor && _resetAt > DateTimeOffset.UtcNow)
        {
            // keep the last few calls for things you click (merge, create pull request)
            if (EtagCache.TryGetValue(url, out var stale)) return stale.Body;
            throw new UserFacingException($"GitHub's API limit is almost used up ({_remaining} calls left) until {_resetAt.ToLocalTime():HH:mm}.",
                "Turning the background fetch interval up in Settings uses fewer calls.");
        }

        using var request = new HttpRequestMessage(method, url);
        if (isGet && EtagCache.TryGetValue(url, out var cached)) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("repo-sync-v3");
        if (body is not null) request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await Forges.Http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Could not reach GitHub: {ex.Message}", "Check the network connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException($"GitHub did not answer within {Forges.Http.Timeout.TotalSeconds:0}s.", "Check the network connection.");
        }

        using (response)
        {
            TrackRateLimit(response);
            if (response.StatusCode == HttpStatusCode.NotModified && EtagCache.TryGetValue(url, out var hit)) return hit.Body;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw ApiError(response, text);
            if (isGet && response.Headers.ETag is { } etag)
            {
                if (EtagCache.Count >= EtagCacheMax) EtagCache.Clear();
                EtagCache[url] = (etag.ToString(), text);
            }
            return text;
        }
    }

    private static void TrackRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && int.TryParse(left.FirstOrDefault(), out var n))
            _remaining = n;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unix))
            _resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    private static UserFacingException ApiError(HttpResponseMessage response, string body)
    {
        var detail = ExtractMessage(body);
        var status = response.StatusCode;
        var rateLimited = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        return status switch
        {
            HttpStatusCode.Unauthorized => new UserFacingException("GitHub rejected the token (401).",
                "It may have expired or been revoked. Click the key button (Accounts) in the toolbar to save a new one."),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when rateLimited => new UserFacingException("GitHub's API rate limit is used up for now.",
                $"It resets at {_resetAt.ToLocalTime():HH:mm}. Turning the background fetch interval up in Settings uses fewer calls."),
            HttpStatusCode.Forbidden => new UserFacingException($"GitHub refused the request (403): {detail}",
                "The token needs the 'repo' and 'workflow' scopes, and you need write access to the repository."),
            HttpStatusCode.NotFound => new UserFacingException($"GitHub says not found (404): {detail}",
                "The repository, branch or pull request may not exist, or the token can't see it (private repos need the 'repo' scope)."),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity =>
                new UserFacingException($"GitHub could not do that ({(int)status}): {detail}",
                    "For a merge: the pull request may not be mergeable yet (required checks, reviews or conflicts)."),
            _ => new UserFacingException($"GitHub API error {(int)status}: {detail}"),
        };
    }

    /// <summary>GitHub errors come as {"message": ..., "errors": [{"message": ...} | {"field", "code"}]}.</summary>
    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var message = root.TryGetProperty("message", out var m) ? GitLabClient.Flatten(m) : string.Empty;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var details = errors.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.Object
                        ? Str(e, "message") ?? string.Join(" ", new[] { Str(e, "field"), Str(e, "code") }.Where(s => s is not null))
                        : e.ToString())
                    .Where(s => s.Length > 0);
                message = string.Join("; ", new[] { message }.Concat(details).Where(s => s.Length > 0));
            }
            if (message.Length > 0) return message;
        }
        catch (JsonException)
        {
            // not JSON - use the raw text
        }
        return body.Length > 300 ? body[..300] : body;
    }
}
