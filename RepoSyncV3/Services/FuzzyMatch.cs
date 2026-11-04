namespace RepoSyncV3.Services;

/// <summary>
/// Forgiving matching for the command palette: "fpl" finds "Fetch, pull", "swmain" finds "Switch to main".
/// Each space-separated word of the query must match the title (or, failing that, the group) as an
/// in-order subsequence. Higher scores mean better matches: whole substrings, word starts and runs of
/// consecutive letters count most, so typing a word prefix ranks above scattered letters.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Score of one query word against one text, or null when the letters don't appear in order.</summary>
    public static int? ScoreWord(string text, string word)
    {
        if (word.Length == 0) return 0;
        if (text.Length == 0) return null;

        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            // a plain substring: best at the very start, then at a word start, then anywhere
            var score = 100 + word.Length * 10;
            if (at == 0) score += 60;
            else if (IsWordStart(text, at)) score += 40;
            return score - Math.Min(at, 30);
        }

        var total = 0;
        var run = 0;
        var ti = 0;
        foreach (var ch in word)
        {
            var found = -1;
            for (var i = ti; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(ch)) continue;
                found = i;
                break;
            }
            if (found < 0) return null;
            run = found == ti && ti > 0 ? run + 1 : 0;
            total += 4 + run * 6 + (IsWordStart(text, found) ? 12 : 0) - Math.Min(found - ti, 8);
            ti = found + 1;
        }
        return total;
    }

    /// <summary>Score of a whole query (space-separated words, any order) against an item, or null when a word misses.</summary>
    public static int? Score(string title, string group, string query)
    {
        var total = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = ScoreWord(title, word) ?? (ScoreWord(group, word) is { } g ? g / 2 : null);
            if (s is null) return null;
            total += s.Value;
        }
        return total;
    }

    /// <summary>The items that match, best first (ties keep their original order). An empty query keeps everything.</summary>
    public static List<T> Filter<T>(IEnumerable<T> items, Func<T, string> title, Func<T, string> group, string query)
    {
        if (query.Trim().Length == 0) return items.ToList();
        return items.Select((item, index) => (item, index, score: Score(title(item), group(item), query)))
                    .Where(x => x.score is not null)
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.index)
                    .Select(x => x.item)
                    .ToList();
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
