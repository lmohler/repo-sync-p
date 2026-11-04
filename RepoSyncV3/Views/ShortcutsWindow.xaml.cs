using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>F1: every keyboard shortcut, from the Shortcuts table plus the few bound directly in MainWindow.xaml.</summary>
public partial class ShortcutsWindow : Window
{
    public sealed record Row(string Keys, string Action);
    public sealed record Group(string Title, IReadOnlyList<Row> Rows);

    private readonly List<Group> _all;

    private ShortcutsWindow()
    {
        InitializeComponent();
        Owner = Application.Current.Dispatcher.CheckAccess() && Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _all = BuildGroups();
        GroupList.ItemsSource = _all;
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>The table's shortcuts by scope, then the fixed ones (profiles, tabs, editing) that aren't in it.</summary>
    public static List<Group> BuildGroups()
    {
        Row R(Shortcut s) => new(s.Text, s.Title);
        return new List<Group>
        {
            new("Every repo / the app", Shortcuts.All.Where(s => s.Scope == Shortcuts.App).Select(R).ToList()),
            new("The selected repo", Shortcuts.All.Where(s => s.Scope == Shortcuts.Repo).Select(R).ToList()),
            new("Profiles and tabs", new List<Row>
            {
                new("Ctrl+0", "All profiles"),
                new("Ctrl+1 ... Ctrl+9", "The profiles in their order (Settings > Profiles)"),
                new("Alt+1 ... Alt+9", "Repo tabs in order"),
                new("Alt+0", "SVN tab"),
            }),
            new("While working", new List<Row>
            {
                new("Ctrl+Enter", "Commit (in the message box)"),
                new("Esc", "Cancel the running git command"),
                new("F1", "This list"),
            }),
        };
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        GroupList.ItemsSource = f.Length == 0
            ? _all
            : _all.Select(g => new Group(g.Title, g.Rows.Where(r => r.Action.Contains(f, StringComparison.OrdinalIgnoreCase)
                                                                  || r.Keys.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList()))
                  .Where(g => g.Rows.Count > 0).ToList();
    }

    public static void Open() => new ShortcutsWindow().ShowDialog();
}
