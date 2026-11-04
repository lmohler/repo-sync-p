using System.Text;

namespace RepoSyncV3.Services;

/// <summary>One list in a <see cref="RewritePreview"/>. Tone: "error" (work that is lost), "warning" (shared
/// history rewritten) or "info".</summary>
public sealed record PreviewSection(string Title, IReadOnlyList<string> Items, string Tone = "warning", string Note = "");

/// <summary>
/// "What will this do" for actions that rewrite history: worked out from the repo before anything runs, so the
/// confirm dialog can list the exact commits rewritten or dropped, the uncommitted files a hard reset throws away,
/// and the server commits a force push overwrites. Read-only: only log / status / rev-list are run.
/// </summary>
public sealed class RewritePreview
{
    private const int MaxItems = 40;

    public required string Title { get; init; }
    public string Summary { get; set; } = string.Empty;
    public List<PreviewSection> Sections { get; } = new();
    /// <summary>Something is lost for good (or overwritten on the server): the OK button turns red.</summary>
    public bool Danger { get; set; }
    /// <summary>Nothing would change; the dialog says so.</summary>
    public bool NothingToDo { get; set; }

    public PreviewSection? Section(string titleStart) => Sections.FirstOrDefault(s => s.Title.StartsWith(titleStart, StringComparison.Ordinal));

    /// <summary>Plain-text version (logs, tests).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Title).Append('\n').Append(Summary).Append('\n');
        foreach (var s in Sections)
        {
            sb.Append('\n').Append(s.Title).Append('\n');
            foreach (var i in s.Items) sb.Append("  ").Append(i).Append('\n');
            if (s.Note.Length > 0) sb.Append("  (").Append(s.Note).Append(")\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ reset

    /// <param name="mode">soft, mixed or hard.</param>
    public static async Task<RewritePreview> ForResetAsync(string path, string target, string mode, CancellationToken ct = default)
    {
        var branch = await BranchAsync(path, ct);
        var p = new RewritePreview { Title = $"Reset ({mode}) '{branch}' to {Short(target)}" };
        var dropped = await CommitsAsync(path, $"{target}..HEAD", ct);
        var gained = await CommitsAsync(path, $"HEAD..{target}", ct);
        p.Summary = mode switch
        {
            "soft" => "The branch moves back; everything those commits changed stays staged, ready to commit again.",
            "hard" => "The branch moves back and every file is made to match that commit.",
            _ => "The branch moves back; everything those commits changed stays in your files, unstaged.",
        };
        if (dropped.Count > 0)
            p.Sections.Add(new PreviewSection($"Commits taken off the branch ({dropped.Count})", Cap(dropped), mode == "hard" ? "error" : "warning",
                mode == "hard" ? "Their changes are thrown away - only the reflog (Recover) can bring them back." : "Their changes stay in your files."));
        if (gained.Count > 0)
            p.Sections.Add(new PreviewSection($"Commits the branch moves onto ({gained.Count})", Cap(gained), "info"));
        await AddPushedAsync(p, path, dropped, ct);
        if (mode == "hard")
        {
            var lost = await UncommittedAsync(path, ct);
            if (lost.Count > 0)
                p.Sections.Add(new PreviewSection($"Uncommitted changes that will be lost ({lost.Count})", Cap(lost), "error",
                    "These were never committed: not even the reflog can get them back. New (untracked) files are left alone."));
            p.Danger = dropped.Count > 0 || lost.Count > 0;
        }
        p.NothingToDo = dropped.Count == 0 && gained.Count == 0 && (mode != "hard" || p.Section("Uncommitted") is null);
        return p;
    }

    // ------------------------------------------------------------------ rebase

    public static async Task<RewritePreview> ForRebaseAsync(string path, string onto, CancellationToken ct = default)
    {
        var branch = await BranchAsync(path, ct);
        var p = new RewritePreview { Title = $"Rebase '{branch}' onto '{onto}'" };
        // git cherry: + = will be replayed as a new commit, - = an equal change is already in onto (dropped)
        var cherry = (await Git.RunAsync(path, new[] { "cherry", "-v", onto, "HEAD" }, ct)).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replayed = cherry.Where(l => l.StartsWith('+')).Select(l => Trim(l[2..])).ToList();
        var already = cherry.Where(l => l.StartsWith('-')).Select(l => Trim(l[2..])).ToList();
        var incoming = await CommitsAsync(path, $"HEAD..{onto}", ct);
        p.Summary = incoming.Count == 0
            ? $"'{branch}' already has everything in '{onto}'."
            : $"Your commits are re-made one by one on top of '{onto}'. They get new hashes; their content stays the same unless a conflict needs fixing.";
        if (replayed.Count > 0) p.Sections.Add(new PreviewSection($"Commits rewritten ({replayed.Count})", Cap(replayed)));
        if (already.Count > 0)
            p.Sections.Add(new PreviewSection($"Commits dropped - already in '{onto}' ({already.Count})", Cap(already), "info"));
        if (incoming.Count > 0) p.Sections.Add(new PreviewSection($"Commits from '{onto}' your branch moves onto ({incoming.Count})", Cap(incoming), "info"));
        var merges = await CommitsAsync(path, $"{onto}..HEAD", ct, "--merges");
        if (merges.Count > 0)
            p.Sections.Add(new PreviewSection($"Merge commits flattened ({merges.Count})", Cap(merges), "warning", "A rebase leaves merge commits out; their changes are kept in the replayed commits."));
        if (incoming.Count > 0) await AddPushedAsync(p, path, await CommitsAsync(path, $"{onto}..HEAD", ct), ct);
        p.NothingToDo = incoming.Count == 0;
        return p;
    }

    /// <summary>Interactive rebase: every commit from the first one the plan changes is rewritten; dropped and
    /// squashed ones are listed on their own.</summary>
    public static async Task<RewritePreview> ForInteractiveRebaseAsync(string path, string onto, IReadOnlyList<RebaseStep> plan, IReadOnlyList<string> originalOrder,
        CancellationToken ct = default)
    {
        var branch = await BranchAsync(path, ct);
        var p = new RewritePreview { Title = $"Rewrite {plan.Count} commit(s) on '{branch}'" };
        static string Line(RebaseStep s) => $"{s.ShortHash} {s.OriginalMessage}";
        static bool Reworded(RebaseStep s) => s.Action == "reword" || (s.Action == "pick" && s.Message.Trim().Length > 0 && s.Message.Trim() != s.OriginalMessage.Trim());

        // the first position where the plan differs from "pick everything as it was"
        var first = 0;
        while (first < plan.Count && first < originalOrder.Count && plan[first].Hash == originalOrder[first] && plan[first].Action == "pick" && !Reworded(plan[first])) first++;
        var unchanged = first >= plan.Count && plan.Count == originalOrder.Count;

        var dropped = plan.Where(s => s.Action == "drop").Select(Line).ToList();
        var squashed = plan.Where(s => s.Action is "squash" or "fixup").Select(s => $"{Line(s)}  ({s.Action} into the commit above)").ToList();
        var reworded = plan.Where(Reworded).Select(s => $"{s.ShortHash} \"{s.OriginalMessage}\" -> \"{s.Message.Trim()}\"").ToList();
        var rewritten = plan.Skip(first).Where(s => s.Action != "drop").Select(Line).ToList();

        p.Summary = unchanged ? "The plan keeps every commit as it is - nothing would change."
            : $"{rewritten.Count} commit(s) get new hashes (everything from the first change down). The commits before that keep theirs.";
        if (dropped.Count > 0) p.Sections.Add(new PreviewSection($"Commits dropped ({dropped.Count})", dropped, "error", "Their changes leave the branch; the reflog (Recover) can still find them."));
        if (squashed.Count > 0) p.Sections.Add(new PreviewSection($"Commits combined ({squashed.Count})", squashed, "warning"));
        if (reworded.Count > 0) p.Sections.Add(new PreviewSection($"Messages changed ({reworded.Count})", reworded, "info"));
        if (rewritten.Count > 0 && !unchanged) p.Sections.Add(new PreviewSection($"Commits rewritten ({rewritten.Count})", Cap(rewritten)));
        if (!unchanged) await AddPushedAsync(p, path, plan.Skip(first).Select(Line).ToList(), ct);
        p.Danger = dropped.Count > 0;
        p.NothingToDo = unchanged;
        return p;
    }

    // ------------------------------------------------------------------ force push

    /// <summary>What <c>git push --force-with-lease remote branch</c> replaces, as of the last fetch.</summary>
    public static async Task<RewritePreview> ForForcePushAsync(string path, string remote, string branch, CancellationToken ct = default)
    {
        var p = new RewritePreview { Title = $"Force push '{branch}' to {remote}" };
        var remoteRef = $"refs/remotes/{remote}/{branch}";
        if (!(await Git.RunAsync(path, new[] { "rev-parse", "-q", "--verify", remoteRef }, ct)).Success)
        {
            p.Summary = $"'{branch}' isn't on {remote} yet (as of the last fetch) - this publishes it and overwrites nothing.";
            var published = await CommitsAsync(path, $"HEAD --not --remotes={remote}", ct);
            if (published.Count > 0) p.Sections.Add(new PreviewSection($"Commits published ({published.Count})", Cap(published), "info"));
            return p;
        }
        var overwritten = await CommitsAsync(path, $"HEAD..{remoteRef}", ct);
        var sent = await CommitsAsync(path, $"{remoteRef}..HEAD", ct);
        p.Summary = $"Uses --force-with-lease: if someone pushed to {remote}/{branch} since your last fetch, the push is refused instead of overwriting their work.";
        if (overwritten.Count > 0)
            p.Sections.Add(new PreviewSection($"Server commits overwritten ({overwritten.Count})", Cap(overwritten), "error",
                "They disappear from the server branch. Anyone who pulled them will have to reset to the new version."));
        if (sent.Count > 0) p.Sections.Add(new PreviewSection($"Your commits that replace them ({sent.Count})", Cap(sent), "info"));
        p.Danger = overwritten.Count > 0;
        p.NothingToDo = overwritten.Count == 0 && sent.Count == 0;
        if (overwritten.Count == 0 && sent.Count > 0) p.Summary = "The server branch is behind yours, so nothing is overwritten - a normal push would do. " + p.Summary;
        return p;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Of <paramref name="commits"/> ("hash subject"), the ones already on a remote branch: rewriting them
    /// means a force push and trouble for anyone who pulled.</summary>
    private static async Task AddPushedAsync(RewritePreview p, string path, IReadOnlyList<string> commits, CancellationToken ct)
    {
        if (commits.Count == 0) return;
        var notOnServer = (await Git.RunAsync(path, new[] { "rev-list", "HEAD", "--not", "--remotes" }, ct)).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var pushed = commits.Where(c => c.Split(' ')[0] is { Length: > 0 } h && !notOnServer.Any(n => n.StartsWith(h, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pushed.Count == 0) return;
        p.Sections.Add(new PreviewSection($"Already on the server ({pushed.Count})", Cap(pushed), "error",
            "Rewriting these needs a Force push afterwards, and changes history other people may have pulled."));
        p.Danger = true;
    }

    private static async Task<List<string>> CommitsAsync(string path, string range, CancellationToken ct, string? extra = null)
    {
        var args = new List<string> { "log", "--format=%h %s" };
        if (extra is not null) args.Add(extra);
        args.AddRange(range.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.Add("--");
        var r = await Git.RunAsync(path, args, ct);
        return r.Success ? r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Trim).ToList() : new List<string>();
    }

    /// <summary>Tracked files with staged or unstaged changes ("M  src/a.cs").</summary>
    private static async Task<List<string>> UncommittedAsync(string path, CancellationToken ct)
    {
        var r = await Git.RunAsync(path, new[] { "status", "--porcelain", "-uno" }, ct);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3)
            .Select(l => $"{l[..2].Trim(),-2} {l[3..]}").ToList();
    }

    private static async Task<string> BranchAsync(string path, CancellationToken ct)
    {
        var b = (await Git.RunAsync(path, new[] { "symbolic-ref", "-q", "--short", "HEAD" }, ct)).StdOut.Trim();
        return b.Length > 0 ? b : "HEAD";
    }

    private static List<string> Cap(List<string> items) =>
        items.Count <= MaxItems ? items : items.Take(MaxItems).Append($"... and {items.Count - MaxItems} more").ToList();

    private static string Trim(string s) => s.Length > 110 ? s[..107] + "..." : s;

    private static string Short(string hash) => hash.Length > 8 && hash.All(Uri.IsHexDigit) ? hash[..8] : hash;
}
