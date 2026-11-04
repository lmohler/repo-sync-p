namespace RepoSyncV3.Services;

public enum RefKind
{
    /// <summary>The checked-out branch (or a bare "HEAD" when detached).</summary>
    Head,
    Local,
    Remote,
    Tag,
}

/// <summary>One label next to a commit in the history list. <see cref="Color"/> is the commit's graph line color index
/// (see GraphCell's palette); <see cref="Name"/> is what `git log` takes to show only this ref.</summary>
public sealed record RefLabel(string Name, RefKind Kind, int Color)
{
    public bool IsBranch => Kind is RefKind.Head or RefKind.Local or RefKind.Remote && Name != "HEAD";
    public bool IsHead => Kind == RefKind.Head;
    public bool IsRemote => Kind == RefKind.Remote;
    public bool IsTag => Kind == RefKind.Tag;
    public bool IsLocal => Kind == RefKind.Local;

    public string ToolTip => Kind switch
    {
        RefKind.Head => Name == "HEAD" ? "HEAD (detached)" : $"{Name} (checked out) - click to show only this branch",
        RefKind.Remote => $"{Name} (remote branch) - click to show only this branch",
        RefKind.Tag => $"tag {Name}",
        _ => $"{Name} (local branch) - click to show only this branch",
    };
}

/// <summary>Splits git's %D decoration ("HEAD -> main, origin/main, tag: v1.2") into typed labels.</summary>
public static class RefLabels
{
    /// <param name="refs">The %D text; Git.GetLogAsync already drops the "HEAD -> " part, which is why the current
    /// branch name is passed in to find HEAD again.</param>
    /// <param name="remotes">Remote names, so "origin/feature" is a remote branch but "feature/x" is local.</param>
    public static List<RefLabel> Parse(string refs, string? currentBranch, IEnumerable<string> remotes, int color)
    {
        var list = new List<RefLabel>();
        if (string.IsNullOrWhiteSpace(refs)) return list;
        var remoteSet = remotes.ToHashSet(StringComparer.Ordinal);
        foreach (var raw in refs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw;
            var kind = RefKind.Local;
            if (name.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                name = name[8..];
                kind = RefKind.Head;
            }
            else if (name.StartsWith("tag: ", StringComparison.Ordinal))
            {
                name = name[5..];
                kind = RefKind.Tag;
            }
            else if (name == "HEAD") kind = RefKind.Head;
            else if (name == currentBranch) kind = RefKind.Head;
            else
            {
                var slash = name.IndexOf('/');
                if (slash > 0 && remoteSet.Contains(name[..slash])) kind = RefKind.Remote;
            }
            // origin/HEAD only says which branch is the server default - noise next to origin/main
            if (kind == RefKind.Remote && name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            list.Add(new RefLabel(name, kind, color));
        }
        // HEAD first, then local, remote, tags - the order the eye wants
        return list.OrderBy(l => (int)l.Kind).ToList();
    }
}
