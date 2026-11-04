using System.IO;
using System.Text;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Services;

public enum ConflictChoice
{
    None,
    Mine,
    Theirs,
    MineThenTheirs,
    TheirsThenMine,
}

/// <summary>One &lt;&lt;&lt;&lt;&lt;&lt;&lt; ... &gt;&gt;&gt;&gt;&gt;&gt;&gt; region. "Mine" is always the branch you're on, also during a rebase
/// (where git writes your side second).</summary>
public sealed class ConflictBlock : ObservableObject
{
    private ConflictChoice _choice;

    public ConflictBlock(int number, IReadOnlyList<string> mine, IReadOnlyList<string> theirs, IReadOnlyList<string> original)
    {
        Number = number;
        Mine = mine;
        Theirs = theirs;
        Original = original;
    }

    public int Number { get; }
    public IReadOnlyList<string> Mine { get; }
    public IReadOnlyList<string> Theirs { get; }

    /// <summary>The block exactly as git wrote it, markers included; kept while no choice is made.</summary>
    public IReadOnlyList<string> Original { get; }

    public string MineText => Text(Mine);
    public string TheirsText => Text(Theirs);

    public ConflictChoice Choice { get => _choice; set { if (Set(ref _choice, value)) OnPropertyChanged(nameof(IsResolved)); } }
    public bool IsResolved => Choice != ConflictChoice.None;

    public IEnumerable<string> Result => Choice switch
    {
        ConflictChoice.Mine => Mine,
        ConflictChoice.Theirs => Theirs,
        ConflictChoice.MineThenTheirs => Mine.Concat(Theirs),
        ConflictChoice.TheirsThenMine => Theirs.Concat(Mine),
        _ => Original,
    };

    private static string Text(IReadOnlyList<string> lines) => string.Join("\n", lines.Select(l => l.TrimEnd('\r', '\n')));
}

/// <summary>A conflicted file split into plain text and conflict blocks, so each block can be resolved on its own.
/// Lines keep their own terminators, so CRLF / LF files are written back byte for byte apart from the choices.</summary>
public sealed class ConflictFile
{
    private readonly List<object> _parts = new(); // string (a line) or ConflictBlock

    public IReadOnlyList<ConflictBlock> Blocks => _parts.OfType<ConflictBlock>().ToList();
    public bool AllResolved => Blocks.All(b => b.IsResolved);

    /// <param name="rebase">During a rebase git writes the upstream first; mine/theirs are swapped back.</param>
    public static ConflictFile Parse(string text, bool rebase)
    {
        var file = new ConflictFile();
        var raw = text.Split('\n');
        var lines = raw.Select((l, i) => i < raw.Length - 1 ? l + "\n" : l).Where(l => l.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Is(lines[i], "<<<<<<<"))
            {
                file._parts.Add(lines[i]);
                continue;
            }
            var start = i;
            var first = new List<string>();
            var second = new List<string>();
            var section = 1; // 1 = first side, 0 = diff3 base (skipped), 2 = second side
            var closed = false;
            for (i++; i < lines.Count; i++)
            {
                if (Is(lines[i], ">>>>>>>")) { closed = true; break; }
                if (section == 1 && Is(lines[i], "|||||||")) section = 0;
                else if (section != 2 && Is(lines[i], "=======")) section = 2;
                else if (section == 1) first.Add(lines[i]);
                else if (section == 2) second.Add(lines[i]);
            }
            var original = lines.Skip(start).Take(i - start + (closed ? 1 : 0)).ToList();
            if (!closed)
            {
                file._parts.AddRange(original); // broken markers: leave the text alone
                break;
            }
            file._parts.Add(rebase
                ? new ConflictBlock(file.Blocks.Count + 1, second, first, original)
                : new ConflictBlock(file.Blocks.Count + 1, first, second, original));
        }
        return file;
    }

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var part in _parts)
        {
            if (part is string line) sb.Append(line);
            else foreach (var l in ((ConflictBlock)part).Result) sb.Append(l);
        }
        return sb.ToString();
    }

    private static bool Is(string line, string marker)
    {
        var t = line.TrimEnd('\r', '\n');
        return t == marker || t.StartsWith(marker + " ", StringComparison.Ordinal);
    }

    /// <summary>Reads a file, remembering whether it had a UTF-8 BOM so <see cref="Write"/> keeps it.</summary>
    public static (string Text, bool Bom) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public static void Write(string path, string text, bool bom) => File.WriteAllText(path, text, new UTF8Encoding(bom));
}
