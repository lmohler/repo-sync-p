using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>Calls one GitLab server's REST API (v4) with a personal access token.</summary>
public sealed class GitLabClient : IForge
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // one /user lookup per server per session
    private static readonly Dictionary<string, ForgeUser> UserCache = new();

    private readonly string _apiBase;
    private readonly string _token;

    public GitLabClient(string apiBase, string token)
    {
        _apiBase = apiBase.TrimEnd('/');
        _token = token;
    }

    public ForgeKind Kind => ForgeKind.GitLab;

    public Task<ForgeUser> GetCurrentUserUncachedAsync(CancellationToken ct = default) => GetAsync<ForgeUser>("user", ct);

    public async Task<ForgeUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        lock (UserCache)
        {
            if (UserCache.TryGetValue(_apiBase, out var cached)) return cached;
        }
        var user = await GetAsync<ForgeUser>("user", ct);
        lock (UserCache) UserCache[_apiBase] = user;
        return user;
    }

    // ---- merge requests ----

    /// <summary>The branch's most relevant merge request (an open one first, else the latest), or null.</summary>
    public async Task<ForgeMergeRequest?> GetBranchMergeRequestAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        var list = await GetAsync<List<ForgeMergeRequest>>(
            $"projects/{remote.ApiId}/merge_requests?source_branch={Uri.EscapeDataString(branch)}&state=all&order_by=updated_at&per_page=5", ct);
        return list.FirstOrDefault(m => m.State == "opened") ?? list.FirstOrDefault();
    }

    public Task<ForgeMergeRequest> GetMergeRequestAsync(ForgeRemote remote, int iid, CancellationToken ct = default) =>
        GetAsync<ForgeMergeRequest>($"projects/{remote.ApiId}/merge_requests/{iid}", ct);

    public async Task<List<ForgeMergeRequest>> ListMergeRequestsAsync(ForgeRemote remote, MergeRequestScope scope, CancellationToken ct = default)
    {
        string query;
        switch (scope)
        {
            case MergeRequestScope.Mine:
                query = $"state=opened&author_username={Uri.EscapeDataString((await GetCurrentUserAsync(ct)).Username)}";
                break;
            case MergeRequestScope.ToReview:
                query = $"state=opened&reviewer_username={Uri.EscapeDataString((await GetCurrentUserAsync(ct)).Username)}";
                break;
            case MergeRequestScope.Merged:
                query = "state=merged";
                break;
            default:
                query = "state=opened";
                break;
        }
        return await GetAsync<List<ForgeMergeRequest>>($"projects/{remote.ApiId}/merge_requests?{query}&order_by=updated_at&per_page=50", ct);
    }

    public Task<ForgeMergeRequest> CreateMergeRequestAsync(
        ForgeRemote remote, string sourceBranch, string targetBranch, string title, string description,
        bool draft, bool removeSourceBranch, bool squash, CancellationToken ct = default) =>
        SendAsync<ForgeMergeRequest>(HttpMethod.Post, $"projects/{remote.ApiId}/merge_requests", new
        {
            source_branch = sourceBranch,
            target_branch = targetBranch,
            title = draft && !title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) ? "Draft: " + title : title,
            description,
            remove_source_branch = removeSourceBranch,
            squash,
        }, ct);

    public Task MergeAsync(ForgeRemote remote, ForgeMergeRequest mr, bool squash, bool removeSourceBranch, CancellationToken ct = default) =>
        SendAsync<ForgeMergeRequest>(HttpMethod.Put, $"projects/{remote.ApiId}/merge_requests/{mr.Iid}/merge", new
        {
            squash,
            should_remove_source_branch = removeSourceBranch,
        }, ct);

    public Task ApproveAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"projects/{remote.ApiId}/merge_requests/{mr.Iid}/approve", null, ct);

    /// <summary>GitLab keeps draft status in the title ("Draft: ...").</summary>
    public Task SetDraftAsync(ForgeRemote remote, ForgeMergeRequest mr, bool draft, CancellationToken ct = default)
    {
        var title = draft
            ? "Draft: " + mr.Title
            : Regex.Replace(mr.Title, @"^\s*(\[draft\]|\(draft\)|draft:|draft)\s*", string.Empty, RegexOptions.IgnoreCase);
        return SendAsync<ForgeMergeRequest>(HttpMethod.Put, $"projects/{remote.ApiId}/merge_requests/{mr.Iid}", new { title }, ct);
    }

    // ---- pipelines ----

    public async Task<ForgePipeline?> GetLatestPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        var list = await GetAsync<List<ForgePipeline>>(
            $"projects/{remote.ApiId}/pipelines?ref={Uri.EscapeDataString(branch)}&per_page=1", ct);
        return list.FirstOrDefault();
    }

    public Task<List<ForgePipeline>> ListPipelinesAsync(ForgeRemote remote, string? branch, CancellationToken ct = default) =>
        GetAsync<List<ForgePipeline>>(
            $"projects/{remote.ApiId}/pipelines?per_page=30{(branch is null ? "" : "&ref=" + Uri.EscapeDataString(branch))}", ct);

    public Task<List<ForgeJob>> ListJobsAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default) =>
        GetAsync<List<ForgeJob>>($"projects/{remote.ApiId}/pipelines/{pipelineId}/jobs?per_page=100&include_retried=false", ct);

    public Task RetryPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default) =>
        SendAsync<ForgePipeline>(HttpMethod.Post, $"projects/{remote.ApiId}/pipelines/{pipelineId}/retry", null, ct);

    public Task CancelPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default) =>
        SendAsync<ForgePipeline>(HttpMethod.Post, $"projects/{remote.ApiId}/pipelines/{pipelineId}/cancel", null, ct);

    public async Task<string> RunPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default)
    {
        var p = await SendAsync<ForgePipeline>(HttpMethod.Post, $"projects/{remote.ApiId}/pipeline?ref={Uri.EscapeDataString(branch)}", null, ct);
        return $"Started pipeline #{p.Id}";
    }

    public Task RetryJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default) =>
        SendAsync<ForgeJob>(HttpMethod.Post, $"projects/{remote.ApiId}/jobs/{jobId}/retry", null, ct);

    public Task PlayJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default) =>
        SendAsync<ForgeJob>(HttpMethod.Post, $"projects/{remote.ApiId}/jobs/{jobId}/play", null, ct);

    /// <summary>The job's console log as plain text (ANSI colors and GitLab's section markers removed).</summary>
    public async Task<string> GetJobLogAsync(ForgeRemote remote, long jobId, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Get, $"projects/{remote.ApiId}/jobs/{jobId}/trace", ct);
        var text = Regex.Replace(raw, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        text = Regex.Replace(text, @"section_(start|end):\d+:[^\r\n]*?\r", string.Empty);
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    // ---- comments / artifacts ----

    public async Task<List<ForgeComment>> ListCommentsAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default)
    {
        var notes = await GetAsync<List<JsonElement>>($"projects/{remote.ApiId}/merge_requests/{mr.Iid}/notes?sort=asc&order_by=created_at&per_page=100", ct);
        var list = new List<ForgeComment>();
        foreach (var n in notes)
        {
            if (n.TryGetProperty("system", out var sys) && sys.ValueKind == JsonValueKind.True) continue;
            var author = n.TryGetProperty("author", out var a) && a.TryGetProperty("name", out var an) ? an.GetString() ?? "?" : "?";
            DateTime? created = n.TryGetProperty("created_at", out var c) && c.TryGetDateTime(out var d) ? d.ToLocalTime() : null;
            string? path = null;
            int? line = null;
            if (n.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                path = pos.TryGetProperty("new_path", out var np) ? np.GetString() : null;
                if (pos.TryGetProperty("new_line", out var nl) && nl.ValueKind == JsonValueKind.Number) line = nl.GetInt32();
                else if (pos.TryGetProperty("old_line", out var ol) && ol.ValueKind == JsonValueKind.Number) line = ol.GetInt32();
            }
            list.Add(new ForgeComment(author, n.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", created, path, line));
        }
        return list;
    }

    public Task AddCommentAsync(ForgeRemote remote, ForgeMergeRequest mr, string body, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"projects/{remote.ApiId}/merge_requests/{mr.Iid}/notes", new { body }, ct);

    public async Task<List<string>> DownloadArtifactsAsync(ForgeRemote remote, long pipelineId, long? jobId, string folder, CancellationToken ct = default)
    {
        if (jobId is null) throw new UserFacingException("Pick a job first - GitLab keeps artifacts per job.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/projects/{remote.ApiId}/jobs/{jobId}/artifacts");
        request.Headers.Add("PRIVATE-TOKEN", _token);
        using var response = await Forges.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UserFacingException("This job has no artifacts (or they expired).");
        if (!response.IsSuccessStatusCode) throw ApiError(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"job-{jobId}-artifacts.zip");
        await using (var fs = File.Create(file)) await response.Content.CopyToAsync(fs, ct);
        return new List<string> { file };
    }

    // ---- projects ----

    /// <summary>Projects the token's user is a member of, most recently active first (at most <paramref name="max"/>).</summary>
    public async Task<List<ForgeProject>> ListMyProjectsAsync(int max = 500, CancellationToken ct = default)
    {
        var all = new List<ForgeProject>();
        for (var page = 1; all.Count < max; page++)
        {
            var batch = await GetAsync<List<ForgeProject>>(
                $"projects?membership=true&simple=true&archived=false&order_by=last_activity_at&per_page=100&page={page}", ct);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    // ---- plumbing ----

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var text = await SendRawAsync(method, path, ct, body);
        if (string.IsNullOrWhiteSpace(text)) text = "{}";
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new UserFacingException($"GitLab returned an empty answer for {path}.");
    }

    private async Task<string> SendRawAsync(HttpMethod method, string path, CancellationToken ct, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"{_apiBase}/{path}");
        request.Headers.Add("PRIVATE-TOKEN", _token);
        if (body is not null) request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await Forges.Http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException($"Could not reach {_apiBase}: {ex.Message}", "Check the network / VPN connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException($"{_apiBase} did not answer within {Forges.Http.Timeout.TotalSeconds:0}s.", "Check the network / VPN connection.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw ApiError(response.StatusCode, text);
            return text;
        }
    }

    private static UserFacingException ApiError(HttpStatusCode status, string body)
    {
        var detail = ExtractMessage(body);
        return status switch
        {
            HttpStatusCode.Unauthorized => new UserFacingException("GitLab rejected the token (401).",
                "It may have expired or been revoked. Click the key button (Accounts) in the toolbar to save a new one."),
            HttpStatusCode.Forbidden => new UserFacingException($"GitLab refused the request (403): {detail}",
                "The token needs the 'api' scope, and your account needs Developer access (or more) to the project."),
            HttpStatusCode.NotFound => new UserFacingException($"GitLab says not found (404): {detail}",
                "The project, branch or merge request may not exist on the server, or the token can't see it."),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotAcceptable or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity =>
                new UserFacingException($"GitLab could not do that ({(int)status}): {detail}",
                    "For a merge: the merge request may not be ready (pipeline, approvals, conflicts or draft)."),
            _ => new UserFacingException($"GitLab API error {(int)status}: {detail}"),
        };
    }

    /// <summary>GitLab errors come as {"message": ...} (a string, a list or a field-to-errors map) or {"error": ...}.</summary>
    internal static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m)) return Flatten(m);
            if (root.TryGetProperty("error", out var e)) return Flatten(e);
        }
        catch (JsonException)
        {
            // not JSON - use the raw text
        }
        return body.Length > 300 ? body[..300] : body;
    }

    internal static string Flatten(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join("; ", e.EnumerateArray().Select(Flatten)),
        JsonValueKind.Object => string.Join("; ", e.EnumerateObject().Select(p => $"{p.Name}: {Flatten(p.Value)}")),
        _ => e.ToString(),
    };
}
