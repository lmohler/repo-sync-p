using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

public enum TokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
}

/// <summary>A piece of a diff line to draw: its token colour and whether it's the part that changed.</summary>
public readonly record struct DiffSegment(string Text, TokenKind Token, bool Changed);

/// <summary>Which tokenizer rules a file gets, picked from its extension.</summary>
public enum SyntaxLang
{
    None,
    CLike,
    Json,
    PowerShell,
    Python,
    Shell,
    Xml,
    Sql,
}

/// <summary>
/// Word-level highlights and light syntax colours for diff lines. Deleted lines are paired with the added lines
/// that follow them (as the split view does); each pair is diffed word by word and every changed run is marked.
/// Colouring is a simple per-language tokenizer (keywords, strings, comments, numbers), not a parser - good enough to read by.
/// </summary>
public static class DiffDecor
{
    /// <summary>Above this share of the line changed, the whole line is the change - no word highlight.</summary>
    private const double MaxChangedShare = 0.7;

    /// <summary>Word diff is O(n*m); past this many tokens a line pair is just shown as changed.</summary>
    private const int MaxTokens = 600;

    public static void Mark(List<DiffLine> lines)
    {
        var ext = string.Empty;
        var deletes = new List<DiffLine>();
        var adds = new List<DiffLine>();

        void Flush()
        {
            for (var i = 0; i < Math.Min(deletes.Count, adds.Count); i++) MarkPair(deletes[i], adds[i]);
            deletes.Clear();
            adds.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Kind == DiffKind.Meta && (line.Text.StartsWith("+++ ", StringComparison.Ordinal) || line.Text.StartsWith("diff --git ", StringComparison.Ordinal)))
                ext = System.IO.Path.GetExtension(line.Text.Split(' ')[^1].Trim('"')).ToLowerInvariant();
            line.Ext = ext;
            if (line.Kind == DiffKind.Delete)
            {
                if (adds.Count > 0) Flush();
                deletes.Add(line);
            }
            else if (line.Kind == DiffKind.Add) adds.Add(line);
            else Flush();
        }
        Flush();
    }

    private static void MarkPair(DiffLine del, DiffLine add)
    {
        // compare without the +/- prefix; offsets below are shifted by 1 back into Text
        var a = del.Text.Length > 0 ? del.Text[1..] : string.Empty;
        var b = add.Text.Length > 0 ? add.Text[1..] : string.Empty;
        if (a == b) return;
        var (spansA, spansB) = WordDiff(a, b);
        if (spansA is null || spansB is null) return;
        if (TooMuch(spansA, a.Length) || TooMuch(spansB, b.Length)) return;
        Apply(del, spansA);
        Apply(add, spansB);
    }

    private static bool TooMuch(List<(int Start, int Length)> spans, int length) =>
        length > 0 && (double)spans.Sum(s => s.Length) / length > MaxChangedShare;

    private static void Apply(DiffLine line, List<(int Start, int Length)> spans)
    {
        if (spans.Count == 0) return;
        line.ChangedSpans = spans.Select(s => (s.Start + 1, s.Length)).ToList();
        var first = line.ChangedSpans[0].Start;
        var last = line.ChangedSpans[^1];
        line.Changed = (first, last.Start + last.Length - first);
    }

    /// <summary>
    /// The changed runs of each side, by a longest-common-subsequence over word / space / punctuation tokens.
    /// Runs split only by whitespace are joined so "a b" -> "x y" is one highlight. Null when the lines are too long to diff.
    /// </summary>
    public static (List<(int Start, int Length)>? A, List<(int Start, int Length)>? B) WordDiff(string a, string b)
    {
        var ta = Words(a);
        var tb = Words(b);
        if (ta.Count > MaxTokens || tb.Count > MaxTokens) return (null, null);

        // lcs[i, j] = common length of ta[i..] and tb[j..]
        var lcs = new int[ta.Count + 1, tb.Count + 1];
        for (var i = ta.Count - 1; i >= 0; i--)
            for (var j = tb.Count - 1; j >= 0; j--)
                lcs[i, j] = Text(a, ta[i]) == Text(b, tb[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var keepA = new bool[ta.Count];
        var keepB = new bool[tb.Count];
        int x = 0, y = 0;
        while (x < ta.Count && y < tb.Count)
        {
            if (Text(a, ta[x]) == Text(b, tb[y])) { keepA[x++] = true; keepB[y++] = true; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
            else y++;
        }
        return (Runs(a, ta, keepA), Runs(b, tb, keepB));
    }

    private static string Text(string s, (int Start, int Length) t) => s.Substring(t.Start, t.Length);

    private static List<(int Start, int Length)> Runs(string s, List<(int Start, int Length)> tokens, bool[] keep)
    {
        var runs = new List<(int Start, int Length)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (keep[i]) continue;
            var (start, len) = tokens[i];
            if (runs.Count > 0)
            {
                // join with the previous run when only kept whitespace sits between them
                var (ps, pl) = runs[^1];
                var gap = s[(ps + pl)..start];
                if (gap.Length == 0 || (gap.Length <= 2 && string.IsNullOrWhiteSpace(gap)))
                {
                    runs[^1] = (ps, start + len - ps);
                    continue;
                }
            }
            runs.Add((start, len));
        }
        // a highlight that starts or ends on whitespace reads as noise; whitespace-only changes vanish here
        for (var i = 0; i < runs.Count; i++)
        {
            var (st, ln) = runs[i];
            while (ln > 0 && char.IsWhiteSpace(s[st])) { st++; ln--; }
            while (ln > 0 && char.IsWhiteSpace(s[st + ln - 1])) ln--;
            runs[i] = (st, ln);
        }
        runs.RemoveAll(r => r.Length == 0);
        return runs;
    }

    /// <summary>Words (letters, digits, _), whitespace runs and single punctuation characters.</summary>
    private static List<(int Start, int Length)> Words(string s)
    {
        var list = new List<(int, int)>();
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            if (IsWord(s[i])) while (i < s.Length && IsWord(s[i])) i++;
            else if (char.IsWhiteSpace(s[i])) while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            else i++;
            list.Add((start, i - start));
        }
        return list;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ------------------------------------------------------------------------------------------- syntax

    public static SyntaxLang LanguageOf(string ext) => ext switch
    {
        ".cs" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".java" or ".kt" or ".go" or ".rs" or ".c" or ".h"
            or ".cpp" or ".hpp" or ".swift" or ".dart" or ".scss" or ".css" or ".less" => SyntaxLang.CLike,
        ".json" or ".jsonc" => SyntaxLang.Json,
        ".ps1" or ".psm1" or ".psd1" => SyntaxLang.PowerShell,
        ".py" => SyntaxLang.Python,
        ".sh" or ".yml" or ".yaml" or ".toml" or ".rb" => SyntaxLang.Shell,
        ".xml" or ".xaml" or ".csproj" or ".vbproj" or ".props" or ".targets" or ".config" or ".resx" or ".svg" or ".html" or ".htm"
            or ".nuspec" or ".manifest" => SyntaxLang.Xml,
        ".sql" => SyntaxLang.Sql,
        _ => SyntaxLang.None,
    };

    private static readonly HashSet<string> CLikeWords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const", "constructor",
        "continue", "decimal", "default", "delegate", "delete", "do", "double", "else", "enum", "event", "export", "extends", "false",
        "finally", "float", "fn", "for", "foreach", "from", "func", "function", "get", "if", "implements", "import", "in", "init", "int",
        "interface", "internal", "is", "let", "long", "namespace", "new", "null", "object", "of", "operator", "out", "override",
        "package", "params", "private", "protected", "public", "readonly", "record", "ref", "required", "return", "sealed", "set",
        "static", "string", "struct", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "using", "var",
        "virtual", "void", "while", "with", "yield", "nameof", "when", "where", "global", "partial", "lock", "uint", "ulong", "short",
    };

    private static readonly HashSet<string> JsonWords = new(StringComparer.Ordinal) { "true", "false", "null" };

    private static readonly HashSet<string> PowerShellWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam", "else", "elseif", "end", "enum", "exit", "filter",
        "finally", "for", "foreach", "function", "if", "in", "param", "process", "return", "switch", "throw", "trap", "try", "until",
        "using", "while", "-eq", "-ne", "-gt", "-ge", "-lt", "-le", "-like", "-notlike", "-match", "-notmatch", "-and", "-or", "-not",
        "-contains", "-notcontains", "-in", "-notin", "-is", "-isnot", "-replace", "-split", "-join", "$true", "$false", "$null",
        "Write-Log",
    };

    private static readonly HashSet<string> PythonWords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False", "finally",
        "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "self",
        "True", "try", "while", "with", "yield",
    };

    private static readonly HashSet<string> ShellWords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "for", "do", "done", "while", "case", "esac", "function", "return", "in", "export", "local",
        "true", "false", "null", "def", "end", "class", "module",
    };

    private static readonly HashSet<string> SqlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "alter", "and", "as", "asc", "begin", "between", "by", "case", "column", "commit", "constraint", "create", "declare",
        "default", "delete", "desc", "distinct", "drop", "else", "end", "exec", "exists", "foreign", "from", "full", "go", "group",
        "having", "if", "in", "index", "inner", "insert", "into", "is", "join", "key", "left", "like", "limit", "merge", "not", "null",
        "on", "or", "order", "outer", "primary", "procedure", "references", "return", "right", "rollback", "select", "set", "table",
        "then", "top", "transaction", "union", "unique", "update", "values", "view", "when", "where", "with", "int", "varchar",
        "nvarchar", "bit", "datetime", "decimal",
    };

    /// <summary>The line split into drawable segments (diff prefix included, as plain text).</summary>
    public static List<DiffSegment> Segments(DiffLine line)
    {
        var text = line.Text;
        var lang = LanguageOf(line.Ext);
        var code = line.Kind is DiffKind.Add or DiffKind.Delete or DiffKind.Context && lang != SyntaxLang.None;
        var tokens = code ? Tokenize(text, 1, lang) : new List<(int, int, TokenKind)> { (0, text.Length, TokenKind.Plain) };
        if (code && text.Length > 0) tokens.Insert(0, (0, 1, TokenKind.Plain));

        // cut tokens at every changed span's edges
        var spans = line.ChangedSpans ?? (line.Changed is { } c ? new List<(int, int)> { c } : new List<(int, int)>());
        var result = new List<DiffSegment>();
        foreach (var (start, length, kind) in tokens)
        {
            var end = start + length;
            var cuts = new List<int> { start, end };
            foreach (var (ss, sl) in spans)
            {
                cuts.Add(Math.Clamp(ss, start, end));
                cuts.Add(Math.Clamp(ss + sl, start, end));
            }
            cuts = cuts.Distinct().Order().ToList();
            for (var i = 0; i < cuts.Count - 1; i++)
            {
                var (from, to) = (cuts[i], cuts[i + 1]);
                if (to <= from) continue;
                var changed = spans.Any(s => from >= s.Item1 && to <= s.Item1 + s.Item2);
                result.Add(new DiffSegment(text[from..to], kind, changed));
            }
        }
        return result;
    }

    /// <summary>Tokens from <paramref name="from"/> on. Single line, so a block comment or string that spans lines is only
    /// coloured on its first line - fine for reading a diff.</summary>
    public static List<(int Start, int Length, TokenKind Kind)> Tokenize(string s, int from, SyntaxLang lang)
    {
        if (lang == SyntaxLang.Xml) return TokenizeXml(s, from);
        var words = lang switch
        {
            SyntaxLang.Json => JsonWords,
            SyntaxLang.PowerShell => PowerShellWords,
            SyntaxLang.Python => PythonWords,
            SyntaxLang.Shell => ShellWords,
            SyntaxLang.Sql => SqlWords,
            _ => CLikeWords,
        };
        var hashComments = lang is SyntaxLang.PowerShell or SyntaxLang.Python or SyntaxLang.Shell;
        var slashComments = lang is SyntaxLang.CLike or SyntaxLang.Json;
        var backslashEscapes = lang is SyntaxLang.CLike or SyntaxLang.Json or SyntaxLang.Python;
        var dashWords = lang == SyntaxLang.PowerShell;

        var list = new List<(int, int, TokenKind)>();
        var plainStart = from;
        void Plain(int upTo)
        {
            if (upTo > plainStart) list.Add((plainStart, upTo - plainStart, TokenKind.Plain));
        }

        var i = from;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            int end;
            TokenKind kind;
            if ((slashComments && c == '/' && next == '/') || (hashComments && c == '#') || (lang == SyntaxLang.Sql && c == '-' && next == '-'))
            {
                end = s.Length;
                kind = TokenKind.Comment;
            }
            else if ((slashComments || lang == SyntaxLang.Sql) && c == '/' && next == '*')
            {
                var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = close < 0 ? s.Length : close + 2;
                kind = TokenKind.Comment;
            }
            else if (c is '"' or '\'' || (c == '`' && lang == SyntaxLang.CLike))
            {
                end = i + 1;
                while (end < s.Length && s[end] != c) end += s[end] == '\\' && backslashEscapes ? 2 : 1;
                end = Math.Min(s.Length, end + 1);
                kind = TokenKind.String;
            }
            else if (char.IsDigit(c) && (i == 0 || !IsWord(s[i - 1])))
            {
                end = i;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '.' or '_')) end++;
                kind = TokenKind.Number;
            }
            else if (IsWord(c) || (dashWords && c is '-' or '$'))
            {
                end = i + 1;
                while (end < s.Length && (IsWord(s[end]) || (dashWords && s[end] == '-'))) end++;
                if (!words.Contains(s[i..end]) || (i > 0 && (IsWord(s[i - 1]) || (s[i - 1] is '$' or '.' && !dashWords))))
                {
                    i = end;
                    continue;
                }
                kind = TokenKind.Keyword;
            }
            else
            {
                i++;
                continue;
            }
            Plain(i);
            list.Add((i, end - i, kind));
            i = plainStart = end;
        }
        Plain(s.Length);
        return list;
    }

    /// <summary>XML / XAML / HTML: comments, tag names (as keywords) and quoted attribute values.</summary>
    private static List<(int Start, int Length, TokenKind Kind)> TokenizeXml(string s, int from)
    {
        var list = new List<(int, int, TokenKind)>();
        var plainStart = from;
        void Plain(int upTo)
        {
            if (upTo > plainStart) list.Add((plainStart, upTo - plainStart, TokenKind.Plain));
        }
        void Add(int start, int end, TokenKind kind)
        {
            Plain(start);
            list.Add((start, end - start, kind));
            plainStart = end;
        }

        var inTag = false;
        var i = from;
        while (i < s.Length)
        {
            var c = s[i];
            if (!inTag && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                var close = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var end = close < 0 ? s.Length : close + 3;
                Add(i, end, TokenKind.Comment);
                i = end;
            }
            else if (!inTag && c == '<')
            {
                inTag = true;
                var start = i + 1;
                if (start < s.Length && s[start] is '/' or '?' or '!') start++;
                var end = start;
                while (end < s.Length && (IsWord(s[end]) || s[end] is ':' or '.' or '-')) end++;
                if (end > start) Add(start, end, TokenKind.Keyword);
                i = Math.Max(end, i + 1);
            }
            else if (inTag && c is '"' or '\'')
            {
                var close = s.IndexOf(c, i + 1);
                var end = close < 0 ? s.Length : close + 1;
                Add(i, end, TokenKind.String);
                i = end;
            }
            else
            {
                if (c == '>') inTag = false;
                i++;
            }
        }
        Plain(s.Length);
        return list;
    }

    // ------------------------------------------------------------------------------------------- hunk navigation

    /// <summary>
    /// The row to jump to for F7 / Shift+F7: the first line of the next (or previous) block of changed lines.
    /// <paramref name="isChange"/> says, per row, whether it's an added / deleted line. Returns -1 when there's none.
    /// </summary>
    public static int NextChange(IReadOnlyList<bool> isChange, int current, bool forward)
    {
        bool IsBlockStart(int i) => isChange[i] && (i == 0 || !isChange[i - 1]);
        if (forward)
        {
            for (var i = Math.Max(0, current + 1); i < isChange.Count; i++)
                if (IsBlockStart(i)) return i;
            return -1;
        }
        // backwards: from inside a block, the previous block (not this one's own start)
        var from = Math.Min(current, isChange.Count) - 1;
        if (current >= 0 && current < isChange.Count && isChange[current])
            while (from >= 0 && isChange[from]) from--;
        for (var i = from; i >= 0; i--)
            if (IsBlockStart(i)) return i;
        return -1;
    }
}
