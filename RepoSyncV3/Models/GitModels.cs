using System.IO;

namespace RepoSyncV3.Models;

public sealed class GitResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;

    public string CombinedOutput => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : $"{StdOut}\n{StdErr}".Trim();
}

public enum LogLevel
{
    Info,
    Command,
    Header,
    Success,
    Warning,
    Error,
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public LogLevel Level { get; }
    public string Message { get; }

    public LogEntry(string message, LogLevel level = LogLevel.Info)
    {
        Message = message;
        Level = level;
    }

    public string Time => Timestamp.ToString("HH:mm:ss");
}

/// <summary>One line of 'git status --porcelain': a file on the staged side, the unstaged side, or in conflict.</summary>
public sealed class FileChange : ITreeFile
{
    /// <summary>Indent level in the tree view (set by FileTreeView).</summary>
    public int TreeDepth { get; set; }

    public required string Path { get; init; }
    public string? OrigPath { get; init; }

    /// <summary>M, A, D, R, C, T, ? (untracked) or ! (conflict).</summary>
    public required char Code { get; init; }

    public bool Staged { get; init; }

    public bool IsUntracked => Code == '?';
    public bool IsConflict => Code == '!';

    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/'));

    public string Folder
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path.Replace('/', '\\'));
            return string.IsNullOrEmpty(dir) ? string.Empty : dir.Replace('\\', '/');
        }
    }

    public string Badge => Code switch
    {
        '?' => "U",
        '!' => "!",
        _ => Code.ToString(),
    };

    public string Label => Code switch
    {
        'M' => "Modified",
        'A' => "Added",
        'D' => "Deleted",
        'R' => OrigPath is null ? "Renamed" : $"Renamed from {OrigPath}",
        'C' => "Copied",
        'T' => "Type changed",
        '?' => "Untracked (new file)",
        '!' => "Conflict",
        _ => Code.ToString(),
    };
}

public sealed class RepoStatus
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Branch { get; set; } = string.Empty;
    public bool Detached { get; set; }
    public string? Upstream { get; set; }
    public bool UpstreamGone { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public List<FileChange> Staged { get; } = new();
    public List<FileChange> Unstaged { get; } = new();
    public List<FileChange> Conflicted { get; } = new();

    public bool IsDirty => Staged.Count + Unstaged.Count + Conflicted.Count > 0;
}

public sealed class BranchInfo
{
    public required string Name { get; init; }
    public bool IsCurrent { get; init; }
    public string? Upstream { get; init; }
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public DateTime? Date { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public bool IsMerged { get; set; }
    public bool HasLocal { get; set; }

    public string Tracking => Upstream is null ? "not published" : UpstreamGone ? $"{Upstream} (deleted on server)" : Upstream;
    public string AheadBehind => Upstream is null || UpstreamGone ? string.Empty : $"{Ahead} / {Behind}";
    public bool IsInSync => Upstream is not null && !UpstreamGone && Ahead == 0 && Behind == 0;
}

public sealed class CommitInfo
{
    public required string Hash { get; init; }
    public required string ShortHash { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Refs { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public bool IsMerge { get; init; }
    public bool IsUnpushed { get; set; }
    public string[] Parents { get; init; } = Array.Empty<string>();
    /// <summary>Set by CommitGraph.Layout; null when the list is filtered.</summary>
    public RepoSyncV3.Services.GraphRow? Graph { get; set; }
    /// <summary><see cref="Refs"/> split into typed, graph-colored labels; set by the history view.</summary>
    public List<RepoSyncV3.Services.RefLabel> Labels { get; set; } = new();
}

public sealed class CommitFile : ITreeFile
{
    /// <summary>Indent level in the tree view (set by FileTreeView).</summary>
    public int TreeDepth { get; set; }

    public required string Path { get; init; }
    public required char Code { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path.Replace('/', '\\'))?.Replace('\\', '/') ?? string.Empty;
}

public sealed class StashInfo
{
    public required string Name { get; init; }
    public DateTime? Date { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class WorktreeInfo
{
    public required string Path { get; init; }
    public string Head { get; set; } = string.Empty;
    public string? Branch { get; set; }
    public bool IsMain { get; init; }
    public bool IsCurrent { get; init; }
    public bool Detached { get; set; }
    public bool Bare { get; set; }
    public bool Locked { get; set; }
    public bool Prunable { get; set; }
    /// <summary>Already a card in the sidebar (set by the view model).</summary>
    public bool InList { get; set; }

    public string ShortHead => Head.Length > 8 ? Head[..8] : Head;
    public string BranchText => Bare ? "(bare)" : Branch ?? $"detached at {ShortHead}";
    public string Status => IsMain ? (IsCurrent ? "main · this one" : "main")
        : Prunable ? "missing - prune"
        : IsCurrent ? "this one"
        : Locked ? "locked"
        : InList ? "in list" : string.Empty;
}

public sealed class TagInfo
{
    public required string Name { get; init; }
    public string Hash { get; init; } = string.Empty;
    public DateTime? Date { get; init; }
    public string Subject { get; init; } = string.Empty;
}

public sealed class BlameLine
{
    public string CommitHash { get; init; } = string.Empty;
    public string ShortHash => CommitHash.Length > 8 ? CommitHash[..8] : CommitHash;
    public string Author { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public int LineNo { get; init; }
    public string Code { get; init; } = string.Empty;
}

public enum DiffKind
{
    Context,
    Add,
    Delete,
    Hunk,
    Meta,
}

public sealed class DiffLine
{
    public required string Text { get; init; }
    public DiffKind Kind { get; init; }
    public string OldNo { get; init; } = string.Empty;
    public string NewNo { get; init; } = string.Empty;
    /// <summary>Which @@ hunk this line belongs to (0-based), -1 outside hunks. Used for partial staging.</summary>
    public int Hunk { get; init; } = -1;
    /// <summary>Position inside the hunk body (0-based); -1 for the @@ line itself.</summary>
    public int Index { get; init; } = -1;
    public bool IsChange => Kind is DiffKind.Add or DiffKind.Delete;

    /// <summary>The part of an added / deleted line that differs from its paired line (offset into <see cref="Text"/>), or null.</summary>
    public (int Start, int Length)? Changed { get; set; }

    /// <summary>Every changed word run of a paired line (offsets into <see cref="Text"/>); <see cref="Changed"/> spans them all.</summary>
    public List<(int Start, int Length)>? ChangedSpans { get; set; }

    /// <summary>The file's extension (".cs"), for syntax colours; empty when unknown.</summary>
    public string Ext { get; set; } = string.Empty;
}

public sealed class SideBySideDiffRow
{
    public DiffLine? LeftLine { get; init; }
    public DiffLine? RightLine { get; init; }

    public bool IsHeader => (LeftLine?.Kind is DiffKind.Hunk or DiffKind.Meta) || (RightLine?.Kind is DiffKind.Hunk or DiffKind.Meta);
    public string HeaderText => LeftLine?.Text ?? RightLine?.Text ?? string.Empty;
    public DiffKind HeaderKind => LeftLine?.Kind ?? RightLine?.Kind ?? DiffKind.Meta;

    public string LeftNo => LeftLine?.OldNo ?? string.Empty;
    public string LeftText => LeftLine?.Text ?? string.Empty;
    public DiffKind LeftKind => LeftLine?.Kind ?? DiffKind.Context;
    public bool HasLeft => LeftLine is not null;

    public string RightNo => RightLine?.NewNo ?? string.Empty;
    public string RightText => RightLine?.Text ?? string.Empty;
    public DiffKind RightKind => RightLine?.Kind ?? DiffKind.Context;
    public bool HasRight => RightLine is not null;

    public static List<SideBySideDiffRow> FromUnified(IEnumerable<DiffLine> unified)
    {
        var rows = new List<SideBySideDiffRow>();
        var pendingDeletes = new List<DiffLine>();
        var pendingAdds = new List<DiffLine>();

        void Flush()
        {
            var count = Math.Max(pendingDeletes.Count, pendingAdds.Count);
            for (var i = 0; i < count; i++)
            {
                var del = i < pendingDeletes.Count ? pendingDeletes[i] : null;
                var add = i < pendingAdds.Count ? pendingAdds[i] : null;
                rows.Add(new SideBySideDiffRow { LeftLine = del, RightLine = add });
            }
            pendingDeletes.Clear();
            pendingAdds.Clear();
        }

        foreach (var line in unified)
        {
            if (line.Kind == DiffKind.Delete)
            {
                pendingDeletes.Add(line);
            }
            else if (line.Kind == DiffKind.Add)
            {
                pendingAdds.Add(line);
            }
            else
            {
                Flush();
                rows.Add(new SideBySideDiffRow { LeftLine = line, RightLine = line });
            }
        }
        Flush();
        return rows;
    }
}

public sealed class ReflogEntry
{
    public required string Hash { get; init; }
    public required string Selector { get; init; }
    public required string Action { get; init; }
    public string When { get; init; } = string.Empty;
    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
}

/// <summary>A file row that the Changes / History file tree can group by folder.</summary>
public interface ITreeFile
{
    string Path { get; }
    string FileName { get; }
    int TreeDepth { get; set; }
}

/// <summary>A tree file with a tick box (SVN send list), so its folder row can tick every file under it.</summary>
public interface ICheckableTreeFile : ITreeFile, System.ComponentModel.INotifyPropertyChanged
{
    bool IsChecked { get; set; }
    bool CanCheck { get; }
}
