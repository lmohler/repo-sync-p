using System.Collections.ObjectModel;
using System.IO;

namespace RepoSyncV3.Services;

/// <summary>One git action the app ran, with the refs (HEAD, current branch, every local branch) before and after.</summary>
public sealed class UndoHistoryEntry
{
    public required string Title { get; init; }
    public required string RepoName { get; init; }
    public required string RepoPath { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>Null when the repo had no commits (or isn't a git repo, e.g. an SVN-only folder).</summary>
    public UndoPoint? Before { get; init; }
    public UndoPoint? After { get; init; }
    public bool Failed { get; init; }
    public bool Undone { get; set; }

    public bool Moved => Before is not null && After is not null && Before.DiffersFrom(After);
    public bool CanUndo => Moved && !Undone;
    public string WhenText => Time.ToString(Time.Date == DateTime.Today ? "HH:mm:ss" : "MMM d HH:mm");

    /// <summary>"main: 1a2b3c4d -> 5e6f7a8b; switched main -> feature/x", or why there's nothing to undo.</summary>
    public string Changes => Undone ? "Undone"
        : Before is null || After is null ? "No refs recorded"
        : Moved ? string.Join("; ", UndoHistory.Describe(Before, After))
        : Failed ? "Failed - nothing moved" : "No branch moved";
}

/// <summary>
/// The session's list of every git action the app ran (newest first), so any of them - not only the last - can be
/// taken back from the Undo history window. Refs only: the snapshots are the same <see cref="UndoPoint"/>s the
/// banner's Undo uses, restored with <see cref="RepoActions.UndoToAsync"/>.
/// </summary>
public sealed class UndoHistory
{
    public const int MaxEntries = 300;

    public ObservableCollection<UndoHistoryEntry> Entries { get; } = new();

    public UndoHistoryEntry Record(string title, string repoName, string repoPath, UndoPoint? before, UndoPoint? after, bool failed = false)
    {
        var e = new UndoHistoryEntry { Title = title, RepoName = repoName, RepoPath = repoPath, Before = before, After = after, Failed = failed };
        Entries.Insert(0, e);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        return e;
    }

    /// <summary>The latest entry of the same repo that still matches <paramref name="before"/> (the banner's Undo).</summary>
    public UndoHistoryEntry? Find(string repoPath, UndoPoint before) =>
        Entries.FirstOrDefault(e => e.RepoPath == repoPath && ReferenceEquals(e.Before, before));

    /// <summary>What changed between two snapshots, one line per ref.</summary>
    public static List<string> Describe(UndoPoint from, UndoPoint to)
    {
        var lines = new List<string>();
        if (from.Branch != to.Branch) lines.Add($"switched {from.Branch ?? Short(from.Head)} -> {to.Branch ?? Short(to.Head)}");
        else if (from.Branch is null && from.Head != to.Head) lines.Add($"HEAD {Short(from.Head)} -> {Short(to.Head)}");
        foreach (var (name, hash) in from.Heads)
        {
            if (!to.Heads.TryGetValue(name, out var now)) lines.Add($"deleted {name} ({Short(hash)})");
            else if (now != hash) lines.Add($"{name}: {Short(hash)} -> {Short(now)}");
        }
        foreach (var (name, hash) in to.Heads.Where(kv => !from.Heads.ContainsKey(kv.Key))) lines.Add($"created {name} ({Short(hash)})");
        return lines;
    }

    /// <summary>
    /// Reasons to stop and ask before undoing <paramref name="entry"/>: refs moved since the action (later actions,
    /// or git outside the app - undoing puts every branch back to <see cref="UndoHistoryEntry.Before"/>, so those
    /// are taken back too), and uncommitted changes (kept, but reset --keep refuses if they touch the same files).
    /// Empty = safe to undo straight away. Throws <see cref="UserFacingException"/> when it can't be undone at all.
    /// </summary>
    public static async Task<List<string>> CheckAsync(UndoHistoryEntry entry, CancellationToken ct = default)
    {
        if (entry.Undone) throw new UserFacingException($"'{entry.Title}' was already undone.");
        if (!entry.Moved || entry.Before is null || entry.After is null) throw new UserFacingException($"'{entry.Title}' didn't move any branch - there's nothing to undo.");
        if (Git.IsRebaseInProgress(entry.RepoPath) || File.Exists(Path.Combine(entry.RepoPath, ".git", "MERGE_HEAD")))
            throw new UserFacingException("A merge or rebase is in progress.", "Continue or Abort it on the Changes tab first.");

        var warnings = new List<string>();
        var now = await UndoPoint.TakeAsync(entry.RepoPath, entry.Title, ct);
        if (now is null) throw new UserFacingException("The repo has no commits any more - nothing to undo.");
        if (now.DiffersFrom(entry.After))
            warnings.Add("Branches moved since this action (a later action, or git outside the app):\n  "
                + string.Join("\n  ", Describe(entry.After, now)) + "\nUndoing puts them back to before this action, so those later changes are taken back too.");
        var dirty = (await Git.RunAsync(entry.RepoPath, new[] { "status", "--porcelain", "-uno" }, ct)).StdOut.Trim();
        if (dirty.Length > 0)
            warnings.Add($"You have uncommitted changes ({dirty.Split('\n').Length} file(s)). They are kept; if they touch files the undo has to change, it stops without changing anything.");
        return warnings;
    }

    private static string Short(string hash) => hash[..Math.Min(8, hash.Length)];
}
