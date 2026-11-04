using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>A step stopped on purpose (bad input, unsafe state, an API or git error). The message is shown to
/// the user as-is, with <see cref="Help"/> lines under it.</summary>
public sealed class UserFacingException : Exception
{
    public IReadOnlyList<string> Help { get; }

    public UserFacingException(string message, params string[] help) : base(message)
    {
        Help = help.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
    }
}

/// <summary>What the app needs from a server: merge requests / pull requests, pipelines / workflow runs, and the
/// projects you can clone. GitLab and GitHub each implement it.</summary>
public interface IForge
{
    ForgeKind Kind { get; }

    Task<ForgeUser> GetCurrentUserAsync(CancellationToken ct = default);
    Task<ForgeUser> GetCurrentUserUncachedAsync(CancellationToken ct = default);

    Task<ForgeMergeRequest?> GetBranchMergeRequestAsync(ForgeRemote remote, string branch, CancellationToken ct = default);
    Task<ForgeMergeRequest> GetMergeRequestAsync(ForgeRemote remote, int iid, CancellationToken ct = default);
    Task<List<ForgeMergeRequest>> ListMergeRequestsAsync(ForgeRemote remote, MergeRequestScope scope, CancellationToken ct = default);
    Task<ForgeMergeRequest> CreateMergeRequestAsync(ForgeRemote remote, string sourceBranch, string targetBranch, string title,
        string description, bool draft, bool removeSourceBranch, bool squash, CancellationToken ct = default);
    Task MergeAsync(ForgeRemote remote, ForgeMergeRequest mr, bool squash, bool removeSourceBranch, CancellationToken ct = default);
    Task ApproveAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default);
    Task SetDraftAsync(ForgeRemote remote, ForgeMergeRequest mr, bool draft, CancellationToken ct = default);

    Task<ForgePipeline?> GetLatestPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default);
    Task<List<ForgePipeline>> ListPipelinesAsync(ForgeRemote remote, string? branch, CancellationToken ct = default);
    Task<List<ForgeJob>> ListJobsAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default);
    Task RetryPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default);
    Task CancelPipelineAsync(ForgeRemote remote, long pipelineId, CancellationToken ct = default);

    /// <summary>Starts a new pipeline / workflow run on the branch; returns what was started.</summary>
    Task<string> RunPipelineAsync(ForgeRemote remote, string branch, CancellationToken ct = default);
    Task RetryJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default);
    Task PlayJobAsync(ForgeRemote remote, long jobId, CancellationToken ct = default);
    Task<string> GetJobLogAsync(ForgeRemote remote, long jobId, CancellationToken ct = default);

    Task<List<ForgeProject>> ListMyProjectsAsync(int max = 500, CancellationToken ct = default);

    /// <summary>Discussion on a merge / pull request, oldest first (system notes left out).</summary>
    Task<List<ForgeComment>> ListCommentsAsync(ForgeRemote remote, ForgeMergeRequest mr, CancellationToken ct = default);
    Task AddCommentAsync(ForgeRemote remote, ForgeMergeRequest mr, string body, CancellationToken ct = default);

    /// <summary>Saves the build artifacts as zip file(s) in folder: GitLab per job, GitHub per run. Returns the files written.</summary>
    Task<List<string>> DownloadArtifactsAsync(ForgeRemote remote, long pipelineId, long? jobId, string folder, CancellationToken ct = default);
}

/// <summary>Picks the client for a remote's server, with the token saved for it.</summary>
public static class Forges
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>A client for the remote's server, or null when no token is saved for it.</summary>
    public static IForge? For(ForgeRemote remote) => For(remote.Kind, remote.Host, remote.BaseUrl);

    public static IForge? For(ForgeKind kind, string host, string? baseUrl = null) =>
        TokenStore.Get(host) is { } token ? Create(kind, host, token, baseUrl) : null;

    public static IForge Create(ForgeKind kind, string host, string token, string? baseUrl = null) => kind == ForgeKind.GitHub
        ? new GitHubClient(token)
        : new GitLabClient($"{baseUrl ?? "https://" + host}/api/v4", token);

    public static IForge Require(ForgeRemote remote) =>
        For(remote) ?? throw new UserFacingException($"No {remote.ServerName} token saved for {remote.Host}.",
            "Click the key button (Accounts) in the toolbar and save a personal access token.");

    /// <summary>"github.com" is GitHub; anything else with a saved token or a GitLab-looking name is GitLab.</summary>
    public static ForgeKind KindOfHost(string host) => ForgeLinks.IsGitHubHost(host) ? ForgeKind.GitHub : ForgeKind.GitLab;
}

/// <summary>
/// Personal access tokens, one per server, kept in tokens.json next to the exe. Each token is encrypted with
/// Windows DPAPI for the current Windows user, so the file is useless on another account or PC.
/// </summary>
public static class TokenStore
{
    private static readonly string TokenFile = Path.Combine(SettingsService.SettingsDir, "tokens.json");
    private static readonly string OldTokenFile = Path.Combine(SettingsService.SettingsDir, "gitlab-tokens.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("repo-sync-v3/gitlab-token");

    private static Dictionary<string, string> LoadAll()
    {
        try
        {
            // v3 before GitHub support kept GitLab tokens in gitlab-tokens.json (same format)
            if (!File.Exists(TokenFile) && File.Exists(OldTokenFile)) File.Move(OldTokenFile, TokenFile);
            if (File.Exists(TokenFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenFile)) ?? new();
        }
        catch
        {
            // unreadable - treat as no tokens
        }
        return new Dictionary<string, string>();
    }

    private static void SaveAll(Dictionary<string, string> all) =>
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

    private static string Key(string host) => host.Trim().ToLowerInvariant();

    public static IReadOnlyList<string> Hosts => LoadAll().Keys.OrderBy(k => k).ToList();

    public static bool Has(string host) => Get(host) is not null;

    public static string? Get(string host)
    {
        if (!LoadAll().TryGetValue(Key(host), out var protectedText)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // saved by another Windows user, or damaged
        }
    }

    public static void Set(string host, string token)
    {
        var all = LoadAll();
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
        all[Key(host)] = Convert.ToBase64String(bytes);
        SaveAll(all);
    }

    public static void Remove(string host)
    {
        var all = LoadAll();
        if (all.Remove(Key(host))) SaveAll(all);
    }
}

/// <summary>Helpers that don't need the API: reading a remote URL, and web page addresses.</summary>
public static class ForgeLinks
{
    private static readonly Regex ScpLikeRegex = new(@"^[^@/]+@(?<host>[^:/]+):(?<path>.+)$", RegexOptions.Compiled);

    /// <summary>Hosts treated as GitLab on top of any host whose name contains "gitlab"; set from settings.</summary>
    public static IReadOnlyCollection<string> ExtraHosts { get; set; } = Array.Empty<string>();

    /// <summary>github.com, and SSH host aliases for it such as "github.com-personal" or "github-work".</summary>
    public static bool IsGitHubHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Split(':')[0].Contains("github", StringComparison.OrdinalIgnoreCase);

    public static bool IsGitLabHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var name = host.Split(':')[0];
        return name.Contains("gitlab", StringComparison.OrdinalIgnoreCase)
            || ExtraHosts.Any(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads https://host[:port]/group/project(.git), ssh://git@host[:port]/group/project(.git) or
    /// git@host:group/project(.git), and decides GitHub or GitLab from the host. Null for anything else.</summary>
    public static ForgeRemote? ParseRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        string host, baseUrl, path;
        var scp = ScpLikeRegex.Match(url);
        if (!url.Contains("://") && scp.Success)
        {
            host = scp.Groups["host"].Value;
            baseUrl = $"https://{host}";
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh")
        {
            host = uri.Host;
            // an ssh port is not the web server's port
            baseUrl = uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}" : $"https://{uri.Host}";
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            return null;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (!path.Contains('/')) return null;

        // only github.com is supported, so an SSH alias like github.com-personal still means github.com
        if (IsGitHubHost(host)) return new ForgeRemote(ForgeKind.GitHub, "github.com", "https://github.com", path);
        return IsGitLabHost(host) ? new ForgeRemote(ForgeKind.GitLab, host, baseUrl, path) : null;
    }

    private static string EscapePath(string branch) => string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));

    public static string Branch(ForgeRemote r, string branch) =>
        r.IsGitHub ? $"{r.WebUrl}/tree/{EscapePath(branch)}" : $"{r.WebUrl}/-/tree/{EscapePath(branch)}";

    public static string Commit(ForgeRemote r, string hash) =>
        r.IsGitHub ? $"{r.WebUrl}/commit/{hash}" : $"{r.WebUrl}/-/commit/{hash}";

    public static string Pipelines(ForgeRemote r, string? branch) => r.IsGitHub
        ? branch is null ? $"{r.WebUrl}/actions" : $"{r.WebUrl}/actions?query={Uri.EscapeDataString("branch:" + branch)}"
        : branch is null ? $"{r.WebUrl}/-/pipelines" : $"{r.WebUrl}/-/pipelines?ref={Uri.EscapeDataString(branch)}";

    public static string MergeRequests(ForgeRemote r) => r.IsGitHub ? $"{r.WebUrl}/pulls" : $"{r.WebUrl}/-/merge_requests";

    public static string Compare(ForgeRemote r, string from, string to) => r.IsGitHub
        ? $"{r.WebUrl}/compare/{EscapePath(from)}...{EscapePath(to)}"
        : $"{r.WebUrl}/-/compare/{EscapePath(from)}...{EscapePath(to)}";

    /// <summary>The page for creating a personal access token with the scopes the app needs filled in.</summary>
    public static string NewToken(ForgeKind kind, string baseUrl) => kind == ForgeKind.GitHub
        ? "https://github.com/settings/tokens/new?description=repo-sync-v3&scopes=repo,workflow,read:org"
        : $"{baseUrl.TrimEnd('/')}/-/user_settings/personal_access_tokens?name=repo-sync-v3&scopes=api";
}

/// <summary>Words that differ between the two servers.</summary>
public static class ForgeTerms
{
    public static string Mr(ForgeKind? k) => k == ForgeKind.GitHub ? "pull request" : "merge request";
    public static string Mrs(ForgeKind? k) => k == ForgeKind.GitHub ? "Pull requests" : "Merge requests";
    public static string MrShort(ForgeKind? k) => k == ForgeKind.GitHub ? "PR" : "MR";
    public static string Pipeline(ForgeKind? k) => k == ForgeKind.GitHub ? "workflow run" : "pipeline";
    public static string Pipelines(ForgeKind? k) => k == ForgeKind.GitHub ? "Actions" : "Pipelines";
    public static string Server(ForgeKind? k) => k switch { ForgeKind.GitHub => "GitHub", ForgeKind.GitLab => "GitLab", _ => "the server" };
}
