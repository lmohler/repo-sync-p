using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views.Tabs;

public partial class HistoryTab : UserControl
{
    public HistoryTab()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= Detail_PropertyChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += Detail_PropertyChanged;
            UpdateGraphColumn();
        };
    }

    /// <summary>DataGrid columns aren't in the visual tree, so ShowGraph is applied here.</summary>
    private void Detail_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoDetailViewModel.ShowGraph)) UpdateGraphColumn();
    }

    private void UpdateGraphColumn() =>
        GraphColumn.Visibility = DataContext is RepoDetailViewModel { ShowGraph: false } ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Keeps a commit picked from elsewhere (e.g. the SVN log's mirror link) in view.</summary>
    private void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: { } item } grid) grid.ScrollIntoView(item);
    }

    private void CommitFileList_DoubleClick(object sender, MouseButtonEventArgs e) => FileTreeInput.ToggleSelectedFolder(CommitFileList);

    private void CommitFileList_PreviewKeyDown(object sender, KeyEventArgs e) => FileTreeInput.PreviewKeyDown(CommitFileList, e);

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not RepoDetailViewModel detail) return;
        e.Handled = true;
        await detail.SearchHistoryAsync();
    }
}
