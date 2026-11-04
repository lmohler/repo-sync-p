using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>
/// Profiles (stored as "workspaces"): any number of named repo lists, each with its own color, git identity, clone
/// folder and background fetch interval. Every repo belongs to exactly one profile, chosen when it is added.
/// </summary>
public static class Workspaces
{
    /// <summary>Set once by the main view model; the settings object is shared and edited in place.</summary>
    public static AppSettings Settings { get; set; } = new();

    public static IReadOnlyList<WorkspaceSettings> All => Settings.Workspaces;

    /// <summary>The sidebar choice that loads every profile at once.</summary>
    public const string AllId = "all";

    /// <summary>Makes the settings consistent: at least one profile, unique ids, every repo in a profile that exists.
    /// Settings from before profiles (repos with no profile) get the old server rule once:
    /// the profile listing the server, else github.com = Personal, GitLab = Work, else the old default.</summary>
    public static void EnsureDefaults(AppSettings settings)
    {
        if (settings.Workspaces.Count == 0) settings.Workspaces = WorkspaceSettings.Defaults();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in settings.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(w.Id) || !seen.Add(w.Id)) w.Id = NewId(settings);
            seen.Add(w.Id);
            if (string.IsNullOrWhiteSpace(w.Name)) w.Name = "Profile";
            // the single clone folder / fetch interval from before profiles becomes every profile's starting point
            if (string.IsNullOrWhiteSpace(w.CloneRoot)) w.CloneRoot = settings.CloneRoot;
            w.AutoFetchMinutes ??= settings.AutoFetchMinutes;
        }

        var ids = settings.Workspaces.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallback = ids.Contains(settings.DefaultWorkspace) ? settings.DefaultWorkspace : settings.Workspaces[0].Id;
        foreach (var entry in settings.Repos.Where(e => e.Workspace is null || !ids.Contains(e.Workspace)))
            entry.Workspace = LegacyRule(settings, entry.Path) ?? fallback;
        foreach (var w in settings.Workspaces) w.Hosts.Clear();

        if (settings.ActiveWorkspace != AllId && !ids.Contains(settings.ActiveWorkspace))
            settings.ActiveWorkspace = settings.Workspaces[0].Id;
    }

    /// <summary>The pre-profile rule, read from the repo's git config (no git process, runs once per repo).</summary>
    private static string? LegacyRule(AppSettings settings, string repoPath)
    {
        if (ReadRemoteUrl(repoPath) is not { } url || ForgeLinks.ParseRemote(url) is not { } remote) return null;
        var listed = settings.Workspaces.FirstOrDefault(w => w.Hosts.Any(h => string.Equals(h.Trim(), remote.Host, StringComparison.OrdinalIgnoreCase)));
        if (listed is not null) return listed.Id;
        var id = remote.Kind switch
        {
            ForgeKind.GitHub => WorkspaceSettings.PersonalId,
            ForgeKind.GitLab => WorkspaceSettings.WorkId,
            _ => null,
        };
        return settings.Workspaces.Any(w => w.Id == id) ? id : null;
    }

    /// <summary>origin's URL, else the first remote's, from the repo's (common) git config.</summary>
    private static string? ReadRemoteUrl(string repoPath)
    {
        try
        {
            if (Git.CommonDir(repoPath) is not { } dir || !File.Exists(Path.Combine(dir, "config"))) return null;
            string? section = null;
            var urls = new List<(string Remote, string Url)>();
            foreach (var raw in File.ReadLines(Path.Combine(dir, "config")))
            {
                var line = raw.Trim();
                var head = Regex.Match(line, @"^\[remote ""(?<name>[^""]+)""\]");
                if (line.StartsWith('[')) section = head.Success ? head.Groups["name"].Value : null;
                else if (section is not null && Regex.Match(line, @"^url\s*=\s*(?<url>.+)$") is { Success: true } m)
                    urls.Add((section, m.Groups["url"].Value.Trim()));
            }
            return urls.FirstOrDefault(u => u.Remote == "origin").Url ?? urls.FirstOrDefault().Url;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string NewId(AppSettings? settings = null)
    {
        var taken = (settings ?? Settings).Workspaces.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string id;
        do id = "p-" + Guid.NewGuid().ToString("N")[..8];
        while (taken.Contains(id));
        return id;
    }

    public static WorkspaceSettings Get(string? id) =>
        All.FirstOrDefault(w => w.Id == id) ?? All.First();

    public static bool Exists(string? id) => All.Any(w => w.Id == id);

    /// <summary>Position in the profile list (sorting and group order in the All view).</summary>
    public static int Order(string id)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Id == id) return i;
        return All.Count;
    }

    public static SolidColorBrush Brush(string? hex, byte alpha = 255)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#888888" : hex.Trim());
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brush("#888888", alpha);
        }
    }
}
