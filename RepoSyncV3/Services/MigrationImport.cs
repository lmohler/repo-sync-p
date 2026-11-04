using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoSyncV3.Models;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Services;

public enum ImportAction
{
    Skip,
    AddExisting,
    Clone,
}

/// <summary>One repo of a migration file and what importing it would do on this computer.</summary>
public sealed class ImportRepoItem : ObservableObject
{
    private readonly Func<string, bool> _inList;
    private string _target;
    private bool _selected;

    public ImportRepoItem(MigrationRepo source, string target, Func<string, bool> inList)
    {
        Source = source;
        CloneUrl = MigrationImport.Unmask(source.CloneUrl);
        _inList = inList;
        _target = target;
        Evaluate();
        _selected = CanImport;
    }

    public MigrationRepo Source { get; }
    public string Name => Source.Name;
    public string? CloneUrl { get; }

    /// <summary>Where the repo goes on this computer; editable in the import window.</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value.Trim())) return;
            Evaluate();
            Selected = CanImport;
        }
    }

    public bool Selected { get => _selected; set => Set(ref _selected, value && CanImport); }
    public ImportAction Action { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public bool CanImport => Action != ImportAction.Skip;

    private void Evaluate()
    {
        (Action, Status) = Decide();
        OnPropertiesChanged(nameof(Action), nameof(Status), nameof(CanImport));
    }

    private (ImportAction, string) Decide()
    {
        if (Target.Length == 0) return (ImportAction.Skip, "No folder");
        if (_inList(Target)) return (ImportAction.Skip, "Already in the list");
        if (Git.IsRepo(Target)) return (ImportAction.AddExisting, "Folder is already here - add it");
        if (Directory.Exists(Target) && Directory.EnumerateFileSystemEntries(Target).Any())
            return (ImportAction.Skip, "Folder exists but isn't a git repo");
        if (Source.WorktreeOf is not null) return (ImportAction.Skip, "Worktree - add it again from the main repo's Worktrees tab");
        if (Source.SvnUrl is not null) return (ImportAction.Skip, $"SVN working copy - check out {Source.SvnUrl} first");
        if (CloneUrl is null) return (ImportAction.Skip, "No remote - copy the folder by hand");
        return (ImportAction.Clone, "Clone");
    }
}

/// <summary>A setting that differs between this computer and the migration file.</summary>
public sealed class SettingDiffItem : ObservableObject
{
    private bool _take;

    public required string Group { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Mine { get; init; }
    public required string Theirs { get; init; }
    /// <summary>False when the file's value can't be used (a masked secret) or the setting is only on this computer.</summary>
    public bool CanTake { get; init; } = true;
    public string Note { get; init; } = string.Empty;

    /// <summary>Take the file's value; otherwise this computer's value is kept.</summary>
    public bool Take { get => _take; set => Set(ref _take, value && CanTake); }

    internal JsonNode? TheirNode { get; init; }
    internal List<string> TheirValues { get; init; } = new();
}

/// <summary>
/// The other half of <see cref="MigrationExport"/>: works out which repos to clone or add, and which app settings
/// and global git config values differ, then applies only what the user picked. Paths under the old user profile
/// are moved to this computer's profile.
/// </summary>
public static class MigrationImport
{
    private const string Masked = "***";
    private const string Profile = "%USERPROFILE%";

    // not worth migrating: window state, UI state, history, the legacy clone folder, and the repo list itself
    private static readonly HashSet<string> SkipSettings = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.Repos), nameof(AppSettings.LastSelectedRepo), nameof(AppSettings.WindowWidth),
        nameof(AppSettings.WindowHeight), nameof(AppSettings.WindowMaximized), nameof(AppSettings.QuickCommandHistory),
        nameof(AppSettings.LogVisible), nameof(AppSettings.ActiveWorkspace), nameof(AppSettings.CloneRoot),
        nameof(AppSettings.ServerWritesSeen),
    };

    public static MigrationFile Read(string path) =>
        JsonSerializer.Deserialize<MigrationFile>(File.ReadAllText(path))
        ?? throw new InvalidDataException("The file is empty.");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The repo's folder on this computer: same place under this user's profile, else the same path.</summary>
    public static string MapPath(MigrationRepo repo, string home) =>
        repo.PathInProfile is { } p && p.StartsWith(Profile, StringComparison.OrdinalIgnoreCase)
            ? home.TrimEnd('\\') + p[Profile.Length..]
            : repo.Path;

    /// <summary>Drops the masked part of a URL so git (and its credential manager) asks for the login again.</summary>
    public static string? Unmask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = Regex.Replace(url, @"://([^:@/\s]+):\*\*\*@", "://$1@");
        return url.Replace("://***@", "://");
    }

    private static string Remap(string value, string oldHome, string home)
    {
        if (string.IsNullOrEmpty(oldHome) || string.Equals(oldHome, home, StringComparison.OrdinalIgnoreCase)) return value;
        value = Regex.Replace(value, Regex.Escape(oldHome.TrimEnd('\\')), home.TrimEnd('\\').Replace("$", "$$"), RegexOptions.IgnoreCase);
        return Regex.Replace(value, Regex.Escape(oldHome.TrimEnd('\\').Replace('\\', '/')), home.TrimEnd('\\').Replace('\\', '/').Replace("$", "$$"), RegexOptions.IgnoreCase);
    }

    public static List<ImportRepoItem> PlanRepos(MigrationFile file, Func<string, bool> inList, string home) =>
        file.Repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ImportRepoItem(r, MapPath(r, home), inList)).ToList();

    // ------------------------------------------------------------------------------------------ app settings

    public static List<SettingDiffItem> DiffSettings(AppSettings mine, MigrationFile file, string home)
    {
        var a = Flatten(JsonSerializer.SerializeToNode(mine)!);
        var b = Flatten(RemapNode(JsonSerializer.SerializeToNode(file.Settings)!, file.UserProfile, home));
        var list = new List<SettingDiffItem>();
        foreach (var (key, theirs) in b)
        {
            a.TryGetValue(key, out var ours);
            if (JsonNode.DeepEquals(ours, theirs)) continue;
            list.Add(new SettingDiffItem
            {
                Group = "App settings",
                Key = key,
                Label = LabelFor(key, mine),
                Mine = Show(ours),
                Theirs = Show(theirs),
                TheirNode = theirs?.DeepClone(),
                CanTake = a.ContainsKey(key),
                Note = a.ContainsKey(key) ? string.Empty : "not used by this version",
            });
        }
        return list;
    }

    public static void ApplySettings(AppSettings mine, IEnumerable<SettingDiffItem> take)
    {
        var root = JsonSerializer.SerializeToNode(mine)!;
        var any = false;
        foreach (var item in take.Where(t => t.Take && t.Group == "App settings"))
        {
            any |= SetAt(root, item.Key, item.TheirNode?.DeepClone());
        }
        if (!any) return;
        var merged = root.Deserialize<AppSettings>()!;
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(AppSettings.Repos)))
            p.SetValue(mine, p.GetValue(merged));
    }

    private static Dictionary<string, JsonNode?> Flatten(JsonNode root)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, value) in root.AsObject())
        {
            if (SkipSettings.Contains(name)) continue;
            Walk(value, name, result);
        }
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, JsonNode?> into)
    {
        switch (node)
        {
            case JsonObject obj:
                // a profile's last open repo is UI state, like the top-level one
                foreach (var (name, value) in obj.Where(p => p.Key != nameof(WorkspaceSettings.LastSelectedRepo)))
                    Walk(value, $"{path}.{name}", into);
                break;
            // lists of objects with an Id (the workspaces) are matched by Id, not position
            case JsonArray arr when arr.Count > 0 && arr.All(e => e is JsonObject o && o["Id"] is JsonValue):
                foreach (var e in arr.OfType<JsonObject>()) Walk(e, $"{path}[{e["Id"]!.GetValue<string>()}]", into);
                break;
            default:
                into[path] = node;
                break;
        }
    }

    private static bool SetAt(JsonNode root, string key, JsonNode? value)
    {
        var parts = key.Split('.');
        JsonNode? node = root;
        for (var i = 0; i < parts.Length - 1 && node is not null; i++) node = Child(node, parts[i]);
        if (node is not JsonObject parent) return false;
        parent[parts[^1]] = value;
        return true;
    }

    private static JsonNode? Child(JsonNode node, string part)
    {
        var m = Regex.Match(part, @"^(?<name>[^\[]+)\[(?<id>[^\]]+)\]$");
        if (!m.Success) return node[part];
        return (node[m.Groups["name"].Value] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(o => o["Id"]?.GetValue<string>() == m.Groups["id"].Value);
    }

    private static JsonNode RemapNode(JsonNode node, string oldHome, string home)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                    if (obj[name] is { } child) obj[name] = RemapNode(child.DeepClone(), oldHome, home);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } child) arr[i] = RemapNode(child.DeepClone(), oldHome, home);
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Remap(s, oldHome, home))!;
            default:
                return node;
        }
    }

    private static string Show(JsonNode? node) => node switch
    {
        null => "(not set)",
        JsonArray { Count: 0 } => "(none)",
        JsonArray arr => string.Join(", ", arr.Select(e => e is JsonValue v && v.TryGetValue<string>(out var s) ? s : e?.ToJsonString())),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length == 0 ? "(empty)" : s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "On" : "Off",
        _ => node.ToJsonString(),
    };

    /// <summary>"Workspaces[work].GitEmail" -> "Work workspace - Git email".</summary>
    private static string LabelFor(string key, AppSettings mine)
    {
        var m = Regex.Match(key, @"^Workspaces\[(?<id>[^\]]+)\]\.(?<field>.+)$");
        if (m.Success)
        {
            var name = mine.Workspaces.FirstOrDefault(w => w.Id == m.Groups["id"].Value)?.Name ?? m.Groups["id"].Value;
            return $"{name} profile - {Humanize(m.Groups["field"].Value)}";
        }
        return Humanize(key);
    }

    private static string Humanize(string name)
    {
        var words = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ").Replace(".", " - ");
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant().Replace("git lab", "GitLab");
    }

    // ------------------------------------------------------------------------------------------ global git config

    public static async Task<List<SettingDiffItem>> DiffGitConfigAsync(MigrationFile file, string home)
    {
        var r = await Git.RunAsync(home, new[] { "config", "--global", "--list" });
        var mine = Group(r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.IndexOf('=') is var eq and >= 0 ? (l[..eq], l[(eq + 1)..]) : (l, string.Empty)));
        var theirs = Group(file.GlobalGitConfig.Select(e => (e.Key, Remap(e.Value, file.UserProfile, home))));
        return DiffGitConfig(mine, theirs);
    }

    public static List<SettingDiffItem> DiffGitConfig(Dictionary<string, List<string>> mine, Dictionary<string, List<string>> theirs)
    {
        var list = new List<SettingDiffItem>();
        foreach (var key in theirs.Keys.Union(mine.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var hasMine = mine.TryGetValue(key, out var ours);
            var hasTheirs = theirs.TryGetValue(key, out var them);
            if (hasMine && hasTheirs && ours!.SequenceEqual(them!)) continue;
            var masked = them?.Any(v => v.Contains(Masked)) == true;
            list.Add(new SettingDiffItem
            {
                Group = "Global git config",
                Key = key,
                Label = key,
                Mine = hasMine ? string.Join("\n", ours!) : "(not set)",
                Theirs = hasTheirs ? string.Join("\n", them!) : "(not set)",
                TheirValues = them ?? new List<string>(),
                CanTake = hasTheirs && !masked,
                Note = !hasTheirs ? "only on this computer - kept" : masked ? "secret - not in the file" : string.Empty,
            });
        }
        return list;
    }

    public static async Task ApplyGitConfigAsync(IEnumerable<SettingDiffItem> take, IProgress<LogEntry> log, string home)
    {
        foreach (var item in take.Where(t => t.Take && t.Group == "Global git config"))
        {
            await Git.RunAsync(home, new[] { "config", "--global", "--unset-all", item.Key });
            foreach (var value in item.TheirValues)
            {
                var r = await Git.RunAsync(home, new[] { "config", "--global", "--add", item.Key, value });
                log.Report(r.Success
                    ? new LogEntry($"git config --global {item.Key} {value}", LogLevel.Command)
                    : new LogEntry($"Could not set {item.Key}: {r.CombinedOutput}", LogLevel.Warning));
            }
        }
    }

    private static Dictionary<string, List<string>> Group(IEnumerable<(string Key, string Value)> entries)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in entries)
        {
            if (!d.TryGetValue(k, out var values)) d[k] = values = new List<string>();
            values.Add(v);
        }
        return d;
    }

    // ------------------------------------------------------------------------------------------ cloning

    /// <summary>After a clone: the other remotes, the branch that was checked out, and the repo's own identity.</summary>
    public static async Task FinishCloneAsync(ImportRepoItem item, IProgress<LogEntry> log)
    {
        var src = item.Source;
        foreach (var remote in src.Remotes.Where(r => r.Name != "origin"))
        {
            if (Unmask(remote.FetchUrl) is not { } url) continue;
            await Logged(item.Target, log, "remote", "add", remote.Name, url);
        }
        foreach (var remote in src.Remotes.Where(r => r.PushUrl is not null))
        {
            if (Unmask(remote.PushUrl) is { } push)
                await Logged(item.Target, log, "remote", "set-url", "--push", remote.Name, push);
        }

        var current = (await Git.RunAsync(item.Target, new[] { "branch", "--show-current" })).StdOut.Trim();
        if (src.CurrentBranch is { Length: > 0 } branch && branch != current
            && (await Git.RunAsync(item.Target, new[] { "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}" })).Success)
        {
            await Logged(item.Target, log, "switch", branch);
        }

        if (src.LocalIdentity is { } id)
        {
            if (id.Name.Length > 0) await Logged(item.Target, log, "config", "--local", "user.name", id.Name);
            if (id.Email.Length > 0) await Logged(item.Target, log, "config", "--local", "user.email", id.Email);
        }
    }

    private static async Task Logged(string path, IProgress<LogEntry> log, params string[] args)
    {
        var r = await Git.RunAsync(path, args);
        log.Report(r.Success
            ? new LogEntry($"git {string.Join(' ', args)}", LogLevel.Command)
            : new LogEntry($"git {string.Join(' ', args)} failed: {r.CombinedOutput}", LogLevel.Warning));
    }
}
