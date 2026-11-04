using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class MainViewModel
{
    // =================================================================================== undo history

    /// <summary>Every git action run through <see cref="RunRepoAsync"/> this session, newest first.</summary>
    public UndoHistory UndoHistory { get; } = new();

    private ICommand? _undoHistoryCommand;

    /// <summary>Opens the Undo history window (parameter: a repo to filter to, else the selected one).</summary>
    public ICommand UndoHistoryCommand => _undoHistoryCommand ??= new RelayCommand(p => OpenUndoHistory(p as RepoViewModel ?? SelectedRepo));

    /// <summary>Called by RunRepoAsync after every action, failed ones too (a rebase that stopped part-way has
    /// still moved HEAD). <paramref name="after"/> is null when the action threw before it was taken.</summary>
    private async Task RecordUndoAsync(RepoViewModel repo, string title, UndoPoint? before, UndoPoint? after, bool ok)
    {
        try
        {
            if (before is not null && after is null) after = await UndoPoint.TakeAsync(repo.Path, title);
        }
        catch
        {
            // folder gone or git missing: the entry just has no "after"
        }
        UndoHistory.Record(title, repo.Name, repo.Path, before, after, failed: !ok);
    }

    private void OpenUndoHistory(RepoViewModel? repo)
    {
        var w = new UndoHistoryWindow(UndoHistory, repo?.Path, UndoEntryAsync, () =>
        {
            if (Detail is { } d && d.RecoverCommand.CanExecute(null)) d.RecoverCommand.Execute(null);
        });
        w.Show();
    }

    /// <summary>Undo one entry: refuses what can't be undone, asks when refs moved since or the tree is dirty, then
    /// puts the refs back with <see cref="RepoActions.UndoToAsync"/>.</summary>
    public async Task<bool> UndoEntryAsync(UndoHistoryEntry entry)
    {
        var repo = Repos.FirstOrDefault(r => string.Equals(r.Path, entry.RepoPath, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
        {
            ShowBanner("Can't undo", $"{entry.RepoName} isn't in the list any more.", "error");
            return false;
        }
        List<string> warnings;
        try
        {
            warnings = await UndoHistory.CheckAsync(entry);
        }
        catch (UserFacingException ex)
        {
            ShowBanner(ex.Message, string.Join("\n", ex.Help), "warning");
            return false;
        }
        var what = string.Join("\n", UndoHistory.Describe(entry.Before!, entry.After!).Select(l => "  " + l));
        var text = $"Put {repo.Name} back to how it was before '{entry.Title}' ({entry.WhenText})?\n\nThis reverses:\n{what}";
        if (warnings.Count > 0) text += "\n\n" + string.Join("\n\n", warnings);
        if (!PromptWindow.Confirm($"Undo {entry.Title.ToLowerInvariant()}", text, "Undo", danger: warnings.Count > 0)) return false;

        var point = entry.Before!;
        var ok = await RunRepoAsync(repo, $"Undo {entry.Title.ToLowerInvariant()}", async a =>
        {
            await a.UndoToAsync(point);
            return "Back to where it was before";
        }, undoable: false);
        if (!ok) return false;
        entry.Undone = true;
        if (ReferenceEquals(repo.LastUndo, point)) repo.LastUndo = null;
        AddLog($"{repo.Name}: undid '{entry.Title}' from {entry.WhenText}.", LogLevel.Success);
        return true;
    }
}
