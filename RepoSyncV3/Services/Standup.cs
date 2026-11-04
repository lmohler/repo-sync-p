using System.Text;

namespace RepoSyncV3.Services;

/// <summary>One commit of yours for the standup / timesheet summary.</summary>
public sealed record StandupCommit(string Repo, DateTime When, string Subject);

/// <summary>"What did I do": your commits on every branch of the given repos since a date, as copyable text.</summary>
public static class Standup
{
    /// <summary>The start of the last workday (Friday on a Monday), so "since yesterday" works after a weekend.</summary>
    public static DateTime LastWorkday(DateTime today)
    {
        var d = today.Date.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    /// <param name="repos">(display name, path, author emails to match).</param>
    public static async Task<List<StandupCommit>> CollectAsync(IEnumerable<(string Name, string Path, IReadOnlyCollection<string> Emails)> repos,
        DateTime since, CancellationToken ct = default)
    {
        var tasks = repos.Select(async r =>
        {
            var emails = r.Emails.Where(e => e.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (emails.Count == 0 || !System.IO.Directory.Exists(r.Path)) return new List<StandupCommit>();
            var args = new List<string> { "log", "--branches", "--no-merges", $"--since={since:yyyy-MM-dd HH:mm}", "--format=%aI%x1f%s" };
            args.AddRange(emails.Select(e => "--author=" + e));
            var result = await Git.RunAsync(r.Path, args, ct);
            var list = new List<StandupCommit>();
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\x1f');
                if (f.Length < 2 || !DateTime.TryParse(f[0], out var when)) continue;
                list.Add(new StandupCommit(r.Name, when, f[1].Trim()));
            }
            return list;
        });
        return (await Task.WhenAll(tasks)).SelectMany(l => l).OrderBy(c => c.Repo).ThenBy(c => c.When).ToList();
    }

    /// <summary>Plain text grouped by repo, ready to paste into a standup or timesheet.</summary>
    public static string Format(IReadOnlyList<StandupCommit> commits, DateTime since)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Since {since:ddd yyyy-MM-dd}:");
        if (commits.Count == 0) sb.AppendLine("  (no commits)");
        foreach (var group in commits.GroupBy(c => c.Repo))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key);
            foreach (var c in group) sb.AppendLine($"  - {c.Subject}  ({c.When:ddd HH:mm})");
        }
        return sb.ToString().TrimEnd();
    }
}
