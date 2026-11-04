using System.Windows;
using System.Windows.Data;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Lists <see cref="UndoHistory"/> with an Undo button per entry. Not modal, so it can stay open while you
/// work; new actions show up at the top as they run.</summary>
public partial class UndoHistoryWindow : Window
{
    private readonly ListCollectionView _view;
    private readonly string? _repoPath;
    private readonly Func<UndoHistoryEntry, Task<bool>> _undo;
    private readonly Action _openReflog;

    public UndoHistoryWindow(UndoHistory history, string? repoPath, Func<UndoHistoryEntry, Task<bool>> undo, Action openReflog)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        _repoPath = repoPath;
        _undo = undo;
        _openReflog = openReflog;
        _view = new ListCollectionView(history.Entries) { Filter = Keep };
        Grid.ItemsSource = _view;
        ThisRepoOnly.IsEnabled = repoPath is not null;
        ThisRepoOnly.IsChecked = repoPath is not null;
    }

    private bool Keep(object o) => o is UndoHistoryEntry e
        && (ThisRepoOnly.IsChecked != true || string.Equals(e.RepoPath, _repoPath, StringComparison.OrdinalIgnoreCase))
        && (MovedOnly.IsChecked != true || e.Moved);

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UndoHistoryEntry entry) return;
        IsEnabled = false;
        try
        {
            await _undo(entry);
        }
        finally
        {
            IsEnabled = true;
            _view.Refresh(); // Undone / CanUndo aren't change-notified
        }
    }

    private void Reflog_Click(object sender, RoutedEventArgs e) => _openReflog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
