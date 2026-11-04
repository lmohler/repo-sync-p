using System.Windows;
using RepoSyncV3.Models;

namespace RepoSyncV3.Views;

/// <summary>A resizable, non-modal window showing one file's diff (unified or split). It is a snapshot of when it was opened.</summary>
public partial class DiffWindow : Window
{
    public DiffWindow(string heading, string subText, List<DiffLine> diff, bool split)
    {
        InitializeComponent();
        if (Application.Current.Dispatcher.CheckAccess())
            Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        Title = $"Diff - {heading}";
        HeadingText.Text = heading;
        SubText.Text = subText;
        UnifiedBox.ItemsSource = diff;
        SplitBox.ItemsSource = SideBySideDiffRow.FromUnified(diff);
        SplitCheck.IsChecked = split;
        ApplyMode();
    }

    private void SplitCheck_Changed(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        if (UnifiedBox is null || SplitBox is null) return;
        var split = SplitCheck.IsChecked == true;
        UnifiedBox.Visibility = split ? Visibility.Collapsed : Visibility.Visible;
        SplitBox.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
