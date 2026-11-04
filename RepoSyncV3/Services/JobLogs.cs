using System.Text.RegularExpressions;

namespace RepoSyncV3.Services;

/// <summary>Cleans CI job logs for display and finds where a failure starts.</summary>
public static partial class JobLogs
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"section_(start|end):\d+:[^\s\r\n]*")]
    private static partial Regex Section();

    [GeneratedRegex(@"(\berror\b|\bfailed\b|\bfailure\b|\bexception\b|^fatal:|npm ERR!|\bFAIL\b|Traceback \(most recent call last\)|\berror [A-Z]+\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLike();

    [GeneratedRegex(@"(\b0 error|\berrors?:\s*0\b|\b0 failed|failed:\s*0\b|--\w*error|error_?reporting|-?\bErrorAction\b|onerror|\bwarn(ing)?\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotAnError();

    /// <summary>Strips ANSI colours and GitLab's collapsible-section markers; keeps the last part of carriage-return progress lines.</summary>
    public static string Clean(string log)
    {
        var text = Section().Replace(Ansi().Replace(log, string.Empty), string.Empty);
        var lines = text.Split('\n').Select(l =>
        {
            var t = l.TrimEnd('\r');
            var i = t.LastIndexOf('\r');
            return i >= 0 ? t[(i + 1)..] : t;
        });
        return string.Join('\n', lines);
    }

    /// <summary>The 0-based line of the first thing that looks like an error, or -1.</summary>
    public static int FirstErrorLine(string cleanLog)
    {
        var lines = cleanLog.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (ErrorLike().IsMatch(lines[i]) && !NotAnError().IsMatch(lines[i])) return i;
        return -1;
    }
}
