using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views.Tabs;

/// <summary>DataContext: RepoDetailViewModel.</summary>
public partial class ChangesTab : UserControl
{
    private bool _syncingSelection;

    public ChangesTab() => InitializeComponent();

    private RepoDetailViewModel? Detail => DataContext as RepoDetailViewModel;

    /// <summary>The conflict, unstaged and staged lists share one "selected file" (for the diff): picking in one
    /// clears the others.</summary>
    private void ChangeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || Detail is not { } detail) return;
        if (sender is not ListBox list || list.SelectedItem is not FileChange file) return;
        _syncingSelection = true;
        try
        {
            foreach (var other in new[] { ConflictList, UnstagedList, StagedList })
            {
                if (other != list) other.SelectedItem = null;
            }
            detail.SelectedChange = file;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void TreeList_PreviewKeyDown(object sender, KeyEventArgs e) => FileTreeInput.PreviewKeyDown((ListBox)sender, e);

    private void UnstagedList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTreeInput.ToggleSelectedFolder(UnstagedList)) return;
        if (UnstagedList.SelectedItem is FileChange f && Detail?.StageCommand.CanExecute(f) == true) Detail.StageCommand.Execute(f);
    }

    private void StagedList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTreeInput.ToggleSelectedFolder(StagedList)) return;
        if (StagedList.SelectedItem is FileChange f && Detail?.UnstageCommand.CanExecute(f) == true) Detail.UnstageCommand.Execute(f);
    }

    private void StageLines_Click(object sender, System.Windows.RoutedEventArgs e) => _ = ApplyLines("stage");

    private void DiscardLines_Click(object sender, System.Windows.RoutedEventArgs e) => _ = ApplyLines("discard");

    private void DiffBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.S) { e.Handled = true; _ = ApplyLines("toggle"); }
        else if (e.Key == Key.U) { e.Handled = true; _ = ApplyLines("unstage"); }
        else if (e.Key == Key.D) { e.Handled = true; _ = ApplyLines("discard"); }
        else if (e.Key == Key.H) { e.Handled = true; SelectHunks(); }
    }

    private void UnstageLines_Click(object sender, System.Windows.RoutedEventArgs e) => _ = ApplyLines("unstage");

    private void ToggleLines_Click(object sender, System.Windows.RoutedEventArgs e) => _ = ApplyLines("toggle");

    private void SelectHunk_Click(object sender, System.Windows.RoutedEventArgs e) => SelectHunks();

    /// <summary>Adds every changed line of the hunk(s) the selection touches (key H), ready for S / U / D.</summary>
    private void SelectHunks()
    {
        var hunks = DiffBox.SelectedItems.OfType<DiffLine>().Where(l => l.Hunk >= 0).Select(l => l.Hunk).ToHashSet();
        foreach (var l in DiffBox.Items.OfType<DiffLine>().Where(l => hunks.Contains(l.Hunk) && l.IsChange).ToList())
            if (!DiffBox.SelectedItems.Contains(l)) DiffBox.SelectedItems.Add(l);
    }

    private async System.Threading.Tasks.Task ApplyLines(string mode)
    {
        if (Detail is null) return;
        await Detail.ApplyLinesAsync(DiffBox.SelectedItems.OfType<DiffLine>().ToList(), mode);
    }

    private void CommitBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var cmd = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Detail?.CommitPushCommand : Detail?.CommitCommand;
            if (cmd?.CanExecute(null) == true) cmd.Execute(null);
        }
    }
}
