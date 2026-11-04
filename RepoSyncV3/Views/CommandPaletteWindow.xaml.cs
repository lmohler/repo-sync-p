using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>One palette row. <see cref="Shortcut"/> is the key text from the Shortcuts table ("" when none).</summary>
public sealed record PaletteItem(string Title, string Group, Action Run, string Shortcut = "");

/// <summary>Ctrl+K: type a few letters of a repo, branch, tab or action and press Enter.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> _items;
    public PaletteItem? Picked { get; private set; }

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _items = items;
        Filter();
        Loaded += (_, _) => Query.Focus();
    }

    /// <summary>Fuzzy: every word typed must appear, letters in order, in the title (or group); best matches first.</summary>
    private void Filter()
    {
        var matches = FuzzyMatch.Filter(_items, i => i.Title, i => i.Group, Query.Text);
        Results.ItemsSource = matches.Take(80).ToList();
        Results.SelectedIndex = 0;
        CountText.Text = matches.Count > 80 ? $"{matches.Count} matches, showing the best 80" : $"{matches.Count} match(es)";
    }

    private void Move(int by)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + by, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(10);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-10);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        Picked = item;
        Close();
    }

    private bool _closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_closing) Close();
    }
}
