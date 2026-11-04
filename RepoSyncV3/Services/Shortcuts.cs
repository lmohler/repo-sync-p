using System.Windows.Input;

namespace RepoSyncV3.Services;

/// <summary>One keyboard shortcut. <see cref="Id"/> is what the toolbar buttons (Shortcut.Id attached property),
/// the main window's key bindings and the command palette look it up by.</summary>
public sealed record Shortcut(string Id, string Title, Key Key, ModifierKeys Modifiers, string Scope)
{
    /// <summary>"Ctrl+Shift+P" - for tooltips and the palette.</summary>
    public string Text => Shortcuts.Format(Key, Modifiers);
}

/// <summary>
/// The single table of the app's keyboard shortcuts. MainWindow binds the keys from it, toolbar tooltips get
/// " (Ctrl+...)" appended from it, and the command palette shows them next to each action. Ctrl+letter alone is
/// left to text boxes (copy, paste, undo...), so actions use Ctrl+Shift (this repo) or Ctrl+Alt (every repo).
/// </summary>
public static class Shortcuts
{
    public const string App = "App";
    public const string Repo = "Repo";

    public static IReadOnlyList<Shortcut> All { get; } = new Shortcut[]
    {
        // ---- app-wide
        new("palette", "Command palette", Key.K, ModifierKeys.Control, App),
        new("help", "Keyboard shortcuts", Key.F1, ModifierKeys.None, App),
        new("quick-command", "Quick git command", Key.OemTilde, ModifierKeys.Control, App),
        new("filter-repos", "Search the repo list", Key.F, ModifierKeys.Control, App),
        new("search", "Search all repos", Key.F, ModifierKeys.Control | ModifierKeys.Shift, App),
        new("home", "Home", Key.Home, ModifierKeys.Alt, App),
        new("overview", "Overview", Key.H, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("refresh-all", "Refresh all", Key.F5, ModifierKeys.None, App),
        new("fetch-all", "Fetch all", Key.F5, ModifierKeys.Shift, App),
        new("pull-all", "Pull all", Key.P, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("push-all", "Push all", Key.U, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("commit-all", "Commit all", Key.C, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("cleanup-all", "Clean up merged branches (all repos)", Key.X, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("standup", "What I did (standup summary)", Key.W, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("clone", "Clone", Key.N, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("add-repo", "Add repo", Key.O, ModifierKeys.Control, App),
        new("scan", "Scan a folder", Key.O, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("add-svn", "Add SVN repo", Key.V, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("accounts", "Accounts", Key.A, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("theme", "Toggle theme", Key.T, ModifierKeys.Control | ModifierKeys.Alt, App),
        new("log", "Show / hide activity log", Key.L, ModifierKeys.Control | ModifierKeys.Shift, App),
        new("settings", "Settings", Key.OemComma, ModifierKeys.Control, App),
        new("undo", "Undo the app's last action", Key.Z, ModifierKeys.Control, App),

        // ---- the selected repo
        new("fetch", "Fetch", Key.E, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("pull", "Pull", Key.P, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("push", "Push", Key.U, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("sync", "Pull & push", Key.Y, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("start-branch", "Start a branch", Key.N, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("switch-branch", "Switch branch (Branches tab)", Key.B, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("update", "Update from default branch", Key.M, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("create-mr", "Create merge request", Key.R, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("finish", "Finish branch", Key.X, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("stage-all", "Stage all", Key.A, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("stash", "Stash changes", Key.S, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("open-folder", "Open folder", Key.O, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("open-editor", "Open in editor", Key.I, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("open-terminal", "Open in terminal", Key.T, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("open-web", "Open on the web", Key.W, ModifierKeys.Control | ModifierKeys.Shift, Repo),
        new("refresh-tab", "Reload this tab", Key.F5, ModifierKeys.Control, Repo),
    };

    private static readonly Dictionary<string, Shortcut> ById = All.ToDictionary(s => s.Id);

    public static Shortcut? Get(string id) => ById.GetValueOrDefault(id);

    /// <summary>The shortcut text for an id, or empty when it has none.</summary>
    public static string TextOf(string id) => Get(id)?.Text ?? string.Empty;

    /// <summary>"Pull (Ctrl+Shift+P)": appends the shortcut unless the text already ends with it.</summary>
    public static string WithShortcut(string? text, string id)
    {
        var keys = TextOf(id);
        if (string.IsNullOrEmpty(text)) return keys.Length == 0 ? string.Empty : $"{Get(id)!.Title} ({keys})";
        if (keys.Length == 0 || text.EndsWith($"({keys})", StringComparison.Ordinal)) return text;
        return $"{text} ({keys})";
    }

    public static string Format(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemTilde => "`",
            Key.Return => "Enter",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>Pairs of ids that share a key combination (should be none; a unit test checks).</summary>
    public static IEnumerable<(string A, string B)> Clashes() =>
        All.GroupBy(s => (s.Key, s.Modifiers)).Where(g => g.Count() > 1)
           .SelectMany(g => g.Skip(1).Select(s => (g.First().Id, s.Id)));
}
