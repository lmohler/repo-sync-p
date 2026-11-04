using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>
/// Search every repo in the workspace at once: commits, branches and merge requests (and, ticked, the code via
/// git grep). Results are grouped by repo; double-click (or Enter) jumps to the repo's tab with the item selected,
/// or opens a code hit in the editor at the line.
/// </summary>
public partial class SearchWindow : Window
{
    private readonly IReadOnlyList<SearchRepo> _repos;
    private readonly string _editor;
    private readonly Action<GlobalHit>? _reveal;
    private CancellationTokenSource? _cts;

    private SearchWindow(string scope, IReadOnlyList<SearchRepo> repos, string editor, Action<GlobalHit>? reveal)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _repos = repos;
        _editor = editor;
        _reveal = reveal;
        ScopeText.Text = $"{repos.Count} repo(s) in {scope}. Commits: the latest {GlobalSearch.CommitsScanned} of every branch per repo. " +
                         $"Up to {GlobalSearch.MaxPerKindPerRepo} hits per kind (code: {RepoSearch.MaxPerRepo}) per repo.";
        Loaded += (_, _) => QueryBox.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var query = QueryBox.Text;
        if (query.Trim().Length == 0) return;
        bool commits = CommitsBox.IsChecked == true, branches = BranchesBox.IsChecked == true,
             mrs = MrsBox.IsChecked == true, code = CodeBox.IsChecked == true;
        if (!commits && !branches && !mrs && !code)
        {
            StatusText.Text = "Tick at least one of Commits, Branches, Merge requests or Code.";
            return;
        }
        StatusText.Text = "Searching...";
        try
        {
            var found = GlobalSearch.SearchAsync(_repos, query, commits, branches, mrs, cts.Token);
            var grep = code
                ? RepoSearch.SearchAsync(_repos.Select(r => (r.Name, r.Path)), query, RegexBox.IsChecked == true, CaseBox.IsChecked == true, cts.Token)
                : Task.FromResult(new List<SearchHit>());
            var hits = (await found).Concat((await grep).Select(GlobalHit.FromCode))
                .OrderBy(h => h.Repo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Kind)
                .ThenByDescending(h => h.Date)
                .ToList();
            if (cts.IsCancellationRequested) return;
            var view = new ListCollectionView(hits);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GlobalHit.Repo)));
            Results.ItemsSource = view;
            var kinds = hits.GroupBy(h => h.KindText).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
            StatusText.Text = hits.Count == 0
                ? "Nothing found."
                : $"{string.Join(", ", kinds)} in {hits.Select(h => h.Repo).Distinct().Count()} repo(s). Double-click or Enter opens it.";
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is not GlobalHit hit) return;
        if (hit.Kind == HitKind.Code)
        {
            Shell.OpenInEditorAt(_editor, System.IO.Path.Combine(hit.RepoPath, hit.Key.Replace('/', '\\')), hit.Line, hit.RepoPath);
            return;
        }
        _reveal?.Invoke(hit);
        Owner?.Activate();
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    /// <param name="reveal">Selects the hit's repo and shows it on its tab (commits, branches, merge requests).</param>
    public static void Open(string scope, IReadOnlyList<SearchRepo> repos, string editor, Action<GlobalHit>? reveal = null) =>
        new SearchWindow(scope, repos, editor, reveal).Show();
}
