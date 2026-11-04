using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace RepoSyncV3.Views;

/// <summary>
/// Side-by-side diff: old on the left, new on the right. Each pane scrolls sideways on its own so long lines
/// never push the other side out of view; vertical scrolling and the selected row are kept in step.
/// </summary>
public partial class SplitDiffView : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SplitDiffView),
        new PropertyMetadata(null, (d, e) =>
        {
            var v = (SplitDiffView)d;
            v.LeftList.ItemsSource = (IEnumerable?)e.NewValue;
            v.RightList.ItemsSource = (IEnumerable?)e.NewValue;
        }));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private bool _syncing;

    public SplitDiffView()
    {
        InitializeComponent();
    }

    private void Pane_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer) return;
        var other = sender == LeftList ? RightList : LeftList;
        if (FindScrollViewer(other) is not { } sv) return;
        _syncing = true;
        try { sv.ScrollToVerticalOffset(e.VerticalOffset); }
        finally { _syncing = false; }
    }

    private void Pane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var other = sender == LeftList ? RightList : LeftList;
        _syncing = true;
        try { other.SelectedIndex = ((ListBox)sender).SelectedIndex; }
        finally { _syncing = false; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
