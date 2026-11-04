using System.Windows;
using System.Windows.Input;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

/// <summary>One repo's outcome in a bulk action. Tone is a Pill tag: "success", "warning" or "error".</summary>
public sealed record BulkResult(RepoViewModel Repo, string Outcome, string Detail, string Tone)
{
    public string Name => Repo.Name;
    public bool Failed => Tone == "error";

    public static BulkResult Done(RepoViewModel r, string detail) => new(r, "Done", detail, "success");
    public static BulkResult Skipped(RepoViewModel r, string why) => new(r, "Skipped", why, "warning");
    public static BulkResult Error(RepoViewModel r, string why) => new(r, "Failed", why, "error");
}

/// <summary>After Pull / Push / Commit all: what happened to each repo, why any were skipped, and Retry for the failed ones.</summary>
public partial class BulkResultsWindow : Window
{
    private readonly Action<RepoViewModel>? _open;
    private bool _retry;

    private BulkResultsWindow(string title, IReadOnlyList<BulkResult> rows, Action<RepoViewModel>? open)
    {
        InitializeComponent();
        Owner = Application.Current.Dispatcher.CheckAccess() && Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _open = open;
        Title = title;
        HeadingText.Text = title;
        var done = rows.Count(r => r.Tone == "success");
        var skipped = rows.Count(r => r.Tone == "warning");
        var failed = rows.Count(r => r.Failed);
        SummaryText.Text = $"{done} done, {skipped} skipped, {failed} failed.";
        // problems first, so they're what you see without scrolling
        RowList.ItemsSource = rows.OrderBy(r => r.Failed ? 0 : r.Tone == "warning" ? 1 : 2).ThenBy(r => r.Name).ToList();
        RetryButton.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowList.SelectedItem is not BulkResult row || _open is null) return;
        _open(row.Repo);
        Close();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _retry = true;
        Close();
    }

    /// <summary>Shows the results; true when Retry failed was clicked.</summary>
    public static bool Show(string title, IReadOnlyList<BulkResult> rows, Action<RepoViewModel>? open = null)
    {
        var w = new BulkResultsWindow(title, rows, open);
        w.ShowDialog();
        return w._retry;
    }
}
