using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Views.Tabs;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>
/// Every view parses and finds its StaticResources. A tab's content is only built when the tab is opened, so a
/// missing style would otherwise first show up as a crash when you click that tab.
/// </summary>
public class XamlLoadTests
{
    private static readonly object AppLock = new();

    public static TheoryData<Type> Views => new()
    {
        typeof(HomeView), typeof(OverviewTab), typeof(ChangesTab), typeof(BranchesTab), typeof(HistoryTab),
        typeof(MergeRequestsTab), typeof(PipelinesTab), typeof(StashesTab), typeof(TagsTab), typeof(SvnTab),
    };

    /// <summary>One Application per AppDomain; it carries the merged theme / window resources.</summary>
    internal static void EnsureApp()
    {
        lock (AppLock)
        {
            if (Application.Current is null) new App().InitializeComponent();
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void View_Loads(Type view)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApp();
                var control = (UserControl)Activator.CreateInstance(view)!;
                control.Measure(new Size(1200, 800));
                control.Arrange(new Rect(0, 0, 1200, 800));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }

    /// <summary>The window's own templates (repo cards, header "More" popup, filter chips) are not covered by the tabs.
    /// Not shown, so InitializeAsync never runs; settings resolve to the test bin folder.</summary>
    [Fact]
    public void MainWindow_Loads()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApp();
                var window = new Views.MainWindow();
                window.Measure(new Size(1480, 900));
                window.Arrange(new Rect(0, 0, 1480, 900));
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }

    /// <summary>The themed ComboBox shows the DisplayMemberPath value in its closed box, not the item's type name.</summary>
    [Fact(Skip = "Flaky: passes alone, fails on CI depending on which tests ran first (shared App thread)")]
    public void ComboBox_ShowsDisplayMember()
    {
        string? shown = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApp();
                var box = new ComboBox { DisplayMemberPath = "Name" };
                box.Items.Add(new Models.WorkspaceSettings { Name = "School" });
                box.SelectedIndex = 0;
                var host = new Window { Content = box, Width = 300, Height = 100, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
                host.Show();
                box.UpdateLayout();
                // let deferred DataBind work (the DisplayMemberPath text) finish before reading the visual tree
                box.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                shown = string.Join("|", Texts(box));
                host.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.Contains("School", shown);
        Assert.DoesNotContain("WorkspaceSettings", shown);
    }

    /// <summary>Dialogs added for usability: they parse, find their styles, and lay out.</summary>
    [Fact]
    public void NewDialogs_Load()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApp();
                var repo = new ViewModels.RepoViewModel(new Models.RepoEntry { Path = @"C:\nowhere\demo" });
                var rows = new List<RepoSyncV3.Views.BulkResult>
                {
                    RepoSyncV3.Views.BulkResult.Done(repo, "Pulled 3 commits"),
                    RepoSyncV3.Views.BulkResult.Skipped(repo, "Has uncommitted changes"),
                    RepoSyncV3.Views.BulkResult.Error(repo, "Authentication failed"),
                };
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var windows = new Window[]
                {
                    (Window)Activator.CreateInstance(typeof(RepoSyncV3.Views.BulkResultsWindow), flags, null,
                        new object?[] { "Pull all: results", rows, null }, null)!,
                    (Window)Activator.CreateInstance(typeof(RepoSyncV3.Views.ShortcutsWindow), true)!,
                };
                foreach (var w in windows)
                {
                    w.Measure(new Size(900, 700));
                    w.Arrange(new Rect(0, 0, 900, 700));
                    w.Close();
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(error is null, error?.InnerException?.ToString() ?? error?.ToString());
    }

    [Fact]
    public void Shortcuts_AreUnique_AndListed()
    {
        var dupes = Services.Shortcuts.All.GroupBy(s => (s.Key, s.Modifiers)).Where(g => g.Count() > 1).Select(g => g.First().Text).ToList();
        Assert.Empty(dupes);
        var listed = RepoSyncV3.Views.ShortcutsWindow.BuildGroups().SelectMany(g => g.Rows).Select(r => r.Action).ToHashSet();
        Assert.All(Services.Shortcuts.All, s => Assert.Contains(s.Title, listed));
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock t) yield return t.Text;
            foreach (var s in Texts(child)) yield return s;
        }
    }
}
