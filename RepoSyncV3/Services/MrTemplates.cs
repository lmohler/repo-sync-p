using System.IO;

namespace RepoSyncV3.Services;

public sealed record MrTemplate(string Name, string Text);

/// <summary>The repo's merge / pull request description templates (GitLab and GitHub locations).</summary>
public static class MrTemplates
{
    public static List<MrTemplate> Find(string repoPath)
    {
        var list = new List<MrTemplate>();
        void AddDir(string dir)
        {
            var full = Path.Combine(repoPath, dir);
            if (!Directory.Exists(full)) return;
            foreach (var f in Directory.EnumerateFiles(full, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                list.Add(new MrTemplate(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f).Trim()));
        }
        void AddFile(string file)
        {
            var full = Path.Combine(repoPath, file);
            if (File.Exists(full) && list.All(t => !string.Equals(t.Name, Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)))
                list.Add(new MrTemplate(Path.GetFileNameWithoutExtension(file), File.ReadAllText(full).Trim()));
        }

        AddDir(".gitlab/merge_request_templates");
        AddDir(".github/PULL_REQUEST_TEMPLATE");
        foreach (var dir in new[] { ".github", "docs", "" })
        {
            AddFile(Path.Combine(dir, "pull_request_template.md"));
            AddFile(Path.Combine(dir, "PULL_REQUEST_TEMPLATE.md"));
        }
        return list;
    }

    /// <summary>The ticket link from the settings pattern, or null.</summary>
    public static string? TicketLink(string pattern, string? ticket) =>
        string.IsNullOrWhiteSpace(pattern) || ticket is null ? null : pattern.Trim().Replace("{ticket}", ticket);
}
