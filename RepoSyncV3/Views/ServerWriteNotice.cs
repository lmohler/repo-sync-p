using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>
/// Creating, approving and merging merge requests / pull requests and posting comments were built against the
/// GitHub and GitLab API docs but haven't been run against a live server yet. The first time you use each one,
/// this says so; after that it stays quiet for that action.
/// </summary>
public static class ServerWriteNotice
{
    /// <param name="action">"create", "approve", "merge" or "comment".</param>
    /// <param name="what">Shown in the message, e.g. "Create merge request".</param>
    /// <returns>false when cancelled.</returns>
    public static bool Confirm(string action, string what)
    {
        var settings = Workspaces.Settings;
        if (settings.ServerWritesSeen.Contains(action)) return true;
        if (!PromptWindow.Confirm($"{what} - first use",
                $"{what} hasn't been tried against a live GitHub or GitLab server yet (it was built from their API docs).\n\n" +
                "Check the result on the web this first time. If something is off, the Activity log has the server's answer.\n\n" +
                "You won't see this note again for this action.", "Go ahead"))
            return false;
        settings.ServerWritesSeen.Add(action);
        SettingsService.Save(settings);
        return true;
    }
}
