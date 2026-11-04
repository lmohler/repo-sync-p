namespace RepoSyncV3.Models;

public sealed class AppSettings
{
    /// <summary>"Light", "Dark", or empty to follow Windows.</summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>New branch names: {name} is what you type, {user} your git user.name.</summary>
    public string BranchPattern { get; set; } = "feature/{name}-{user}";

    /// <summary>Background fetch interval from before profiles; now only the starting value for new profiles.</summary>
    public int AutoFetchMinutes { get; set; } = 10;

    /// <summary>Command that opens a folder or file in your editor; the path is appended in quotes.</summary>
    public string EditorCommand { get; set; } = "code";

    /// <summary>Link to a ticket, {ticket} = the key from the branch name (e.g. https://app.clickup.com/t/{ticket}); empty = none.</summary>
    public string TicketUrl { get; set; } = string.Empty;

    /// <summary>Clone folder used before workspaces existed; each workspace now has its own.</summary>
    public string CloneRoot { get; set; } = string.Empty;

    /// <summary>Tray balloon when a pipeline you're watching finishes or a merge request is merged.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>Minimizing hides the window to the tray icon (double-click or its menu brings it back).</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>The home page's Get started checklist was closed before finishing it.</summary>
    public bool GetStartedDismissed { get; set; }

    /// <summary>New merge requests: delete the source branch when merged.</summary>
    public bool RemoveSourceBranch { get; set; } = true;

    /// <summary>New merge requests: squash commits when merged.</summary>
    public bool SquashOnMerge { get; set; }

    /// <summary>Server write actions ("create", "approve", "merge", "comment") whose first-use note was shown.</summary>
    public List<string> ServerWritesSeen { get; set; } = new();

    /// <summary>Commands typed in the quick git bar, oldest first (the last 50).</summary>
    public List<string> QuickCommandHistory { get; set; } = new();

    /// <summary>Hosts treated as GitLab even though their name doesn't contain "gitlab".</summary>
    public List<string> ExtraGitLabHosts { get; set; } = new();

    /// <summary>Profiles: name, color, git identity, clone folder, fetch interval. At least one.</summary>
    public List<WorkspaceSettings> Workspaces { get; set; } = WorkspaceSettings.Defaults();

    /// <summary>Before profiles: where repos with no server rule went. Only read when old settings are migrated.</summary>
    public string DefaultWorkspace { get; set; } = WorkspaceSettings.PersonalId;

    /// <summary>What happens when a commit's author email doesn't match the repo's workspace:
    /// "block" (stop and ask), "warn" (banner, commit anyway) or "off".</summary>
    public string IdentityCheck { get; set; } = "block";

    /// <summary>The loaded profile's id, or "all" to load every profile.</summary>
    public string ActiveWorkspace { get; set; } = "all";

    public List<RepoEntry> Repos { get; set; } = new();

    /// <summary>Repo last open in the "All profiles" view (each profile keeps its own).</summary>
    public string? LastSelectedRepo { get; set; }
    public bool LogVisible { get; set; } = true;
    public bool SplitDiff { get; set; }
    /// <summary>Changes and History file lists grouped by folder.</summary>
    public bool FileTree { get; set; }
    public double WindowWidth { get; set; } = 1480;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    /// <summary>Smaller row heights and padding everywhere (Settings > Appearance).</summary>
    public bool CompactDensity { get; set; }

    /// <summary>Workspace overview grouping: "none", "workspace", "server" or "status".</summary>
    public string OverviewGroupBy { get; set; } = "workspace";
}

public sealed class WorkspaceSettings
{
    public const string PersonalId = "personal";
    public const string WorkId = "work";

    /// <summary>Stable key that repos point to ("personal" / "work" for the original two, "p-xxxxxxxx" for new ones).</summary>
    public string Id { get; set; } = PersonalId;

    public string Name { get; set; } = "Personal";

    /// <summary>#RRGGBB used for the workspace's tag and group header.</summary>
    public string Color { get; set; } = "#2F80ED";

    /// <summary>The git author for commits in this workspace's repos; empty = no check.</summary>
    public string GitName { get; set; } = string.Empty;
    public string GitEmail { get; set; } = string.Empty;

    /// <summary>Where Clone puts new repos of this workspace (a subfolder per project).</summary>
    public string CloneRoot { get; set; } = string.Empty;

    /// <summary>Before profiles: servers whose repos belonged here. Only read when old settings are migrated.</summary>
    public List<string> Hosts { get; set; } = new();

    public bool Notifications { get; set; } = true;

    /// <summary>Background fetch + server refresh of this profile's watched repos while it is loaded; 0 = off,
    /// null = not set yet (filled from the old global value).</summary>
    public int? AutoFetchMinutes { get; set; }

    /// <summary>Path of the repo last open in this profile (reopened when you switch back); null = home page.</summary>
    public string? LastSelectedRepo { get; set; }

    public static List<WorkspaceSettings> Defaults() => new()
    {
        new WorkspaceSettings { Id = PersonalId, Name = "Personal", Color = "#2F80ED" },
        new WorkspaceSettings { Id = WorkId, Name = "Work", Color = "#0F9D8A" },
    };
}

public sealed class RepoEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Optional label shown instead of the folder name.</summary>
    public string? DisplayName { get; set; }

    public bool Pinned { get; set; }

    /// <summary>Included in background fetch / server refresh; null = on.</summary>
    public bool? Watched { get; set; }

    /// <summary>Id of the profile this repo belongs to (null only in settings from before profiles).</summary>
    public string? Workspace { get; set; }

    /// <summary>Command run in the repo folder before every push (e.g. "dotnet test"); a non-zero exit stops the push.</summary>
    public string? PrePushCommand { get; set; }

    /// <summary>Background fetch also fast-forwards the branch when it's clean, has nothing to push, and only needs pulling.</summary>
    public bool AutoFastForward { get; set; }

    /// <summary>Avatar color "#RRGGBB" chosen by hand; null = picked from the name.</summary>
    public string? Color { get; set; }
}
