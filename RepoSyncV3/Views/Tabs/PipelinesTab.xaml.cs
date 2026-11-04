using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views.Tabs;

public partial class PipelinesTab : UserControl
{
    public PipelinesTab() => InitializeComponent();

    private int ErrorLine => (DataContext as RepoDetailViewModel)?.JobLogErrorLine ?? -1;

    /// <summary>A failed job opens at its first error; anything else at the end, where failures usually are.</summary>
    private void JobLogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorLine >= 0) GoToLine(ErrorLine);
        else JobLogBox.ScrollToEnd();
    }

    private void FirstError_Click(object sender, RoutedEventArgs e)
    {
        if (ErrorLine >= 0) GoToLine(ErrorLine);
    }

    private void End_Click(object sender, RoutedEventArgs e) => JobLogBox.ScrollToEnd();

    private void GoToLine(int line)
    {
        if (line >= JobLogBox.LineCount) return;
        var start = JobLogBox.GetCharacterIndexFromLineIndex(line);
        JobLogBox.Select(start, JobLogBox.GetLineLength(line));
        // show a few lines of context above the error
        JobLogBox.ScrollToLine(Math.Max(0, line - 3));
        JobLogBox.ScrollToLine(Math.Min(JobLogBox.LineCount - 1, line + 15));
        JobLogBox.ScrollToLine(Math.Max(0, line - 3));
    }
}
