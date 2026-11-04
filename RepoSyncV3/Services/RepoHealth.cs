using System.IO;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>One line of the health report. Tone: success, warning, error or info (matches the Pill style).</summary>
public sealed record HealthItem(string Tone, string Title, string Detail);

/// <summary>Housekeeping checks for one repo: size, branches to clean up, big files, old stashes, LFS.</summary>
public static class RepoHealth
{
    public static async Task<List<HealthItem>> CheckAsync(string path, string defaultRef, CancellationToken ct = default)
    {
        var items = new List<HealthItem>();

        // .git size
        var count = await Git.RunAsync(path, new[] { "count-objects", "-v" }, ct);
        long kib = 0;
        foreach (var line in count.StdOut.Split('\n'))
        {
            var p = line.Split(':', 2);
            if (p.Length == 2 && p[0].Trim() is "size" or "size-pack" && long.TryParse(p[1].Trim(), out var v)) kib += v;
        }
        var mb = kib / 1024.0;
        items.Add(new HealthItem(mb > 1024 ? "warning" : "success", $".git is {mb:0.#} MB",
            mb > 1024 ? "Large history. 'git gc' or a shallow clone can help; big binaries belong in Git LFS." : "Size is fine."));

        // branches
        var branches = await Git.GetLocalBranchesAsync(path, ct);
        var merged = await Git.GetMergedBranchesAsync(path, defaultRef, ct);
        var defaultName = defaultRef.Contains('/') ? defaultRef[(defaultRef.IndexOf('/') + 1)..] : defaultRef;
        var cleanable = branches.Where(b => !b.IsCurrent && b.Name != defaultName && (merged.Contains(b.Name) || b.UpstreamGone)).Select(b => b.Name).ToList();
        items.Add(cleanable.Count == 0
            ? new HealthItem("success", "No merged or orphaned branches", "Nothing to clean up.")
            : new HealthItem("warning", $"{cleanable.Count} branch(es) can be deleted", "Merged into " + defaultRef + " or deleted on the server: " + string.Join(", ", cleanable.Take(15)) + (cleanable.Count > 15 ? ", ..." : "")));
        var stale = branches.Where(b => !b.IsCurrent && b.Name != defaultName && !cleanable.Contains(b.Name) && b.Date < DateTime.Now.AddDays(-90)).ToList();
        if (stale.Count > 0)
            items.Add(new HealthItem("info", $"{stale.Count} branch(es) untouched for 90+ days", string.Join(", ", stale.Take(15).Select(b => $"{b.Name} ({b.Date:yyyy-MM-dd})"))));
        var unpublished = branches.Where(b => b.Upstream is null && b.Name != defaultName).ToList();
        if (unpublished.Count > 0)
            items.Add(new HealthItem("info", $"{unpublished.Count} branch(es) only on this PC", "Not pushed anywhere - lost if this disk dies: " + string.Join(", ", unpublished.Take(15).Select(b => b.Name))));

        // big files
        var big = await Git.GetLargeFilesAsync(path, 10L * 1024 * 1024, ct: ct);
        var lfs = File.Exists(Path.Combine(path, ".gitattributes")) && (await File.ReadAllTextAsync(Path.Combine(path, ".gitattributes"), ct)).Contains("filter=lfs");
        items.Add(big.Count == 0
            ? new HealthItem("success", "No files over 10 MB", lfs ? "Git LFS is set up for this repo." : "Good.")
            : new HealthItem(big.Any(b => b.Bytes > 100L * 1024 * 1024) ? "error" : "warning", $"{big.Count} file(s) over 10 MB in the current tree",
                string.Join("\n", big.Take(10).Select(b => $"{b.Path} ({b.Bytes / 1024.0 / 1024:0.#} MB)")) + (lfs ? "" : "\nConsider Git LFS: git lfs track \"*.ext\"")));

        // stashes
        var stashes = await Git.GetStashesAsync(path, ct);
        var old = stashes.Count(s => s.Date < DateTime.Now.AddDays(-30));
        if (stashes.Count > 0)
            items.Add(new HealthItem(old > 0 ? "warning" : "info", $"{stashes.Count} stash(es)", old > 0 ? $"{old} older than 30 days - apply or drop them on the Stashes tab." : "All recent."));

        // worktrees
        var prunable = (await Git.GetWorktreesAsync(path, ct)).Count(w => w.Prunable);
        if (prunable > 0) items.Add(new HealthItem("warning", $"{prunable} missing worktree(s)", "Their folders are gone - Prune on the Worktrees tab."));
        return items;
    }
}
