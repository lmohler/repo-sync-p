using System.ComponentModel;
using System.Windows;
using RepoSyncV3.Services;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

/// <summary>Commit every repo with changes at once: tick which, check each message, optionally push.</summary>
public partial class CommitAllWindow : Window
{
    private readonly IReadOnlyList<CommitAllRow> _rows;

    private CommitAllWindow(string scope, IReadOnlyList<CommitAllRow> rows)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _rows = rows;
        RowList.ItemsSource = rows;
        ScopeText.Text = $"{rows.Count} repo(s) with changes in {scope}. Each ticked repo stages everything (git add -A) and commits with its message.";
        foreach (var r in rows) r.PropertyChanged += Row_Changed;
        Update();
    }

    private void Row_Changed(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var n = _rows.Count(r => r.Include);
        OkButton.Content = n == 0 ? "Commit" : $"Commit {n} repo(s)";
        OkButton.IsEnabled = n > 0 && _rows.Where(r => r.Include).All(r => r.Message.Trim().Length > 0);
    }

    private void UseForAll_Click(object sender, RoutedEventArgs e)
    {
        var message = AllMessageBox.Text.Trim();
        if (message.Length == 0) return;
        foreach (var r in _rows.Where(r => r.Include))
            r.Message = Git.TicketFromBranch(r.Repo.Branch) is { } t && !message.Contains(t, StringComparison.OrdinalIgnoreCase) ? $"{t} {message}" : message;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>The ticked rows and whether to push, or null when cancelled.</summary>
    public static (List<CommitAllRow> Rows, bool Push)? Ask(string scope, IReadOnlyList<CommitAllRow> rows)
    {
        var w = new CommitAllWindow(scope, rows);
        return w.ShowDialog() == true ? (rows.Where(r => r.Include).ToList(), w.PushBox.IsChecked == true) : null;
    }
}
