using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// Everything needed to set up the same repos on another computer: each repo's remotes, branches and identity,
/// the app settings, the global git config and which accounts / SSH keys to bring along. Credentials are never
/// written (tokens are DPAPI-bound to this PC anyway, and passwords in remote URLs are masked), and each repo
/// lists the work that only exists here - uncommitted changes, stashes, unpushed or local-only branches.
/// </summary>
public static class MigrationExport
{
    private const char Sep = '\x1f';

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // keys of the global config whose values are secrets
    private static readonly Regex SecretKey = new(@"token|password|passwd|secret|extraheader|signingkey", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlUserInfo = new(@"(?<scheme>[a-z][a-z0-9+.-]*://)(?<info>[^@/\s]+)@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string DefaultFileName() => $"repo-sync-export-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.json";

    public static async Task<MigrationFile> BuildAsync(AppSettings settings, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var file = new MigrationFile
        {
            ExportedAt = DateTime.Now,
            Machine = Environment.MachineName,
            WindowsUser = Environment.UserName,
            UserProfile = home,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            GitVersion = (await Git.RunAsync(home, new[] { "--version" }, ct)).StdOut.Trim(),
            GlobalGitConfig = await GetGlobalConfigAsync(home, ct),
            AccountHosts = TokenStore.Hosts.ToList(),
            SshKeys = SshKeyFiles(home),
            SshHostAliases = SshHostAliases(home),
            Settings = CopySettings(settings),
        };

        var total = settings.Repos.Count;
        var done = 0;
        using var gate = new SemaphoreSlim(6);
        var tasks = settings.Repos.Select(async entry =>
        {
            await gate.WaitAsync(ct);
            try { return await DescribeRepoAsync(entry, home, ct); }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), total));
            }
        }).ToList();
        file.Repos = (await Task.WhenAll(tasks)).ToList();
        return file;
    }

    public static async Task WriteAsync(MigrationFile file, string path) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, JsonOptions));

    private static async Task<MigrationRepo> DescribeRepoAsync(RepoEntry entry, string home, CancellationToken ct)
    {
        var path = entry.Path;
        var repo = new MigrationRepo
        {
            Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? Path.GetFileName(path.TrimEnd('\\')) : entry.DisplayName!,
            Path = path,
            PathInProfile = InProfile(path, home),
            DisplayName = entry.DisplayName,
            Pinned = entry.Pinned,
            Watched = entry.Watched,
            WorkspaceOverride = entry.Workspace,
        };

        if (!Git.IsRepo(path))
        {
            repo.Exists = false;
            repo.Warnings.Add("Folder is missing or is no longer a git repository.");
            return repo;
        }
        repo.Exists = true;

        if (Git.MainWorktreePath(path) is { } main && !string.Equals(main.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            repo.WorktreeOf = main;

        // remotes
        var remotes = await Git.RunAsync(path, new[] { "remote", "-v" }, ct);
        foreach (var line in Lines(remotes.StdOut))
        {
            var m = Regex.Match(line, @"^(?<name>\S+)\s+(?<url>\S+)\s+\((?<kind>fetch|push)\)$");
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            var url = Redact(m.Groups["url"].Value, out var masked);
            if (masked && !repo.Warnings.Contains(MaskedWarning)) repo.Warnings.Add(MaskedWarning);
            var remote = repo.Remotes.FirstOrDefault(r => r.Name == name);
            if (remote is null) repo.Remotes.Add(remote = new MigrationRemote { Name = name });
            if (m.Groups["kind"].Value == "fetch") remote.FetchUrl = url;
            else remote.PushUrl = url;
        }
        foreach (var r in repo.Remotes.Where(r => r.PushUrl == r.FetchUrl)) r.PushUrl = null;
        if (repo.Remotes.Count == 0) repo.Warnings.Add("No remote: this repo only exists on this computer. Copy the folder.");

        var origin = repo.Remotes.FirstOrDefault(r => r.Name == "origin") ?? repo.Remotes.FirstOrDefault();
        repo.CloneUrl = origin?.FetchUrl;
        repo.Workspace = entry.Workspace ?? string.Empty;

        repo.CurrentBranch = (await Git.RunAsync(path, new[] { "branch", "--show-current" }, ct)).StdOut.Trim() is { Length: > 0 } b ? b : null;

        var status = await Git.RunAsync(path, new[] { "status", "--porcelain" }, ct);
        repo.UncommittedFiles = Lines(status.StdOut).Count();
        if (repo.UncommittedFiles > 0) repo.Warnings.Add($"{repo.UncommittedFiles} file(s) with uncommitted changes.");

        // a linked worktree shares branches, stashes and config with its main repo, which reports them
        if (repo.WorktreeOf is not null) return repo;

        // branches and what isn't on any remote yet
        var refs = await Git.RunAsync(path, new[] { "for-each-ref", "refs/heads", $"--format=%(refname:short){Sep}%(upstream:short){Sep}%(upstream:track)" }, ct);
        foreach (var line in Lines(refs.StdOut))
        {
            var parts = line.Split(Sep);
            var branch = new MigrationBranch
            {
                Name = parts[0],
                Upstream = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                UpstreamGone = parts.Length > 2 && parts[2].Contains("gone"),
            };
            var count = await Git.RunAsync(path, new[] { "rev-list", "--count", $"refs/heads/{branch.Name}", "--not", "--remotes" }, ct);
            branch.CommitsNotOnServer = int.TryParse(count.StdOut.Trim(), out var n) ? n : 0;
            repo.Branches.Add(branch);

            if (repo.Remotes.Count > 0 && branch.CommitsNotOnServer > 0)
            {
                repo.Warnings.Add(branch.Upstream is null || branch.UpstreamGone
                    ? $"Branch '{branch.Name}' is local only ({branch.CommitsNotOnServer} commit(s)). Push it first."
                    : $"Branch '{branch.Name}' has {branch.CommitsNotOnServer} unpushed commit(s).");
            }
        }

        var stashes = await Git.RunAsync(path, new[] { "stash", "list" }, ct);
        repo.Stashes = Lines(stashes.StdOut).Count();
        if (repo.Stashes > 0) repo.Warnings.Add($"{repo.Stashes} stash(es) - stashes are never pushed.");

        var localName = (await Git.RunAsync(path, new[] { "config", "--local", "user.name" }, ct)).StdOut.Trim();
        var localEmail = (await Git.RunAsync(path, new[] { "config", "--local", "user.email" }, ct)).StdOut.Trim();
        if (localName.Length > 0 || localEmail.Length > 0)
            repo.LocalIdentity = new MigrationIdentity { Name = localName, Email = localEmail };

        repo.HasSubmodules = File.Exists(Path.Combine(path, ".gitmodules"));
        repo.UsesLfs = File.Exists(Path.Combine(path, ".gitattributes"))
            && (await File.ReadAllTextAsync(Path.Combine(path, ".gitattributes"), ct)).Contains("filter=lfs");

        if (Svn.IsWorkingCopy(path))
        {
            try
            {
                repo.SvnUrl = (await Svn.RunAsync(path, new[] { "info", "--show-item", "url" }, ct: ct)).StdOut.Trim() is { Length: > 0 } u ? u : null;
            }
            catch
            {
                // svn not installed; the .svn folder still says it's a working copy
            }
            repo.Warnings.Add("SVN working copy: check it out from SVN first, then add the git side.");
        }

        return repo;
    }

    private const string MaskedWarning = "A remote URL had a password or token in it; it was masked in this file.";

    private static async Task<List<MigrationConfigEntry>> GetGlobalConfigAsync(string home, CancellationToken ct)
    {
        var r = await Git.RunAsync(home, new[] { "config", "--global", "--list" }, ct);
        var list = new List<MigrationConfigEntry>();
        foreach (var line in Lines(r.StdOut))
        {
            var eq = line.IndexOf('=');
            var key = eq < 0 ? line : line[..eq];
            var value = eq < 0 ? string.Empty : line[(eq + 1)..];
            value = SecretKey.IsMatch(key) ? "***" : Redact(value, out _);
            list.Add(new MigrationConfigEntry { Key = key, Value = value });
        }
        return list;
    }

    /// <summary>https://user:secret@host -> https://user:***@host; a bare token as the user name is masked too.</summary>
    public static string Redact(string url, out bool masked)
    {
        var hit = false;
        var result = UrlUserInfo.Replace(url, m =>
        {
            var info = m.Groups["info"].Value;
            var colon = info.IndexOf(':');
            if (colon >= 0)
            {
                hit = true;
                return $"{m.Groups["scheme"].Value}{info[..colon]}:***@";
            }
            if (info.Length >= 20 || info.StartsWith("ghp_") || info.StartsWith("github_pat_") || info.StartsWith("glpat-"))
            {
                hit = true;
                return $"{m.Groups["scheme"].Value}***@";
            }
            return m.Value;
        });
        masked = hit;
        return result;
    }

    private static string? InProfile(string path, string home) =>
        path.StartsWith(home.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[home.TrimEnd('\\').Length..]
            : null;

    private static AppSettings CopySettings(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        copy.Repos = new List<RepoEntry>(); // listed in full under "Repos"
        copy.LastSelectedRepo = null;
        return copy;
    }

    private static List<string> SshKeyFiles(string home)
    {
        var dir = Path.Combine(home, ".ssh");
        if (!Directory.Exists(dir)) return new List<string>();
        // names only - the keys themselves have to be copied by hand
        return Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) || n is "config")
            .OrderBy(n => n).ToList();
    }

    private static List<string> SshHostAliases(string home)
    {
        var config = Path.Combine(home, ".ssh", "config");
        if (!File.Exists(config)) return new List<string>();
        try
        {
            return File.ReadAllLines(config)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
                .SelectMany(l => l[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(h => h != "*")
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class MigrationFile
{
    public string Notes { get; set; } =
        "Exported by repo-sync. Tokens are not included (re-add them under Accounts); copy your SSH keys (~/.ssh) by hand; " +
        "anything listed under a repo's Warnings only exists on the old computer.";
    public DateTime ExportedAt { get; set; }
    public string Machine { get; set; } = string.Empty;
    public string WindowsUser { get; set; } = string.Empty;
    public string UserProfile { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string GitVersion { get; set; } = string.Empty;

    /// <summary>Servers with a saved token; the tokens themselves stay on this PC.</summary>
    public List<string> AccountHosts { get; set; } = new();
    public List<string> SshKeys { get; set; } = new();
    public List<string> SshHostAliases { get; set; } = new();
    public List<MigrationConfigEntry> GlobalGitConfig { get; set; } = new();

    public int RepoCount => Repos.Count;
    public int ReposNeedingAttention => Repos.Count(r => r.Warnings.Count > 0);
    public List<MigrationRepo> Repos { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public sealed class MigrationRepo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    /// <summary>The same path with the profile folder as %USERPROFILE%, for a PC with another user name.</summary>
    public string? PathInProfile { get; set; }
    public bool Exists { get; set; }
    public string? CloneUrl { get; set; }
    public List<MigrationRemote> Remotes { get; set; } = new();
    public string? CurrentBranch { get; set; }
    public List<MigrationBranch> Branches { get; set; } = new();
    public string Workspace { get; set; } = string.Empty;
    public string? WorkspaceOverride { get; set; }
    public string? DisplayName { get; set; }
    public bool Pinned { get; set; }
    public bool? Watched { get; set; }
    public MigrationIdentity? LocalIdentity { get; set; }
    public string? WorktreeOf { get; set; }
    public string? SvnUrl { get; set; }
    public bool HasSubmodules { get; set; }
    public bool UsesLfs { get; set; }
    public int UncommittedFiles { get; set; }
    public int Stashes { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class MigrationRemote
{
    public string Name { get; set; } = string.Empty;
    public string? FetchUrl { get; set; }
    /// <summary>Only when it differs from the fetch URL.</summary>
    public string? PushUrl { get; set; }
}

public sealed class MigrationBranch
{
    public string Name { get; set; } = string.Empty;
    public string? Upstream { get; set; }
    public bool UpstreamGone { get; set; }
    public int CommitsNotOnServer { get; set; }
}

public sealed class MigrationIdentity
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class MigrationConfigEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
