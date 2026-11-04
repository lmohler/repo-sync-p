using System.Windows;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

/// <summary>A checklist dialog: tick some of the given items.</summary>
public partial class ScanResultsWindow : Window
{
    // public: WPF bindings can't read properties of a private type
    public sealed class Item : ObservableObject
    {
        private bool _checked = true;
        public required string Text { get; init; }
        /// <summary>Repo (folder) name shown in its own column; empty for plain lists such as branch names.</summary>
        public string Name { get; init; } = string.Empty;
        public bool HasName => Name.Length > 0;
        public bool Checked { get => _checked; set => Set(ref _checked, value); }
    }

    private readonly List<Item> _items;

    private ScanResultsWindow(IEnumerable<string> items, bool repoNames)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _items = items.Select(i => new Item { Text = i, Name = repoNames ? System.IO.Path.GetFileName(i.TrimEnd('\\')) : string.Empty }).ToList();
        HeaderRow.Visibility = repoNames ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.ItemsSource = _items;
    }

    private void All_Click(object sender, RoutedEventArgs e) => _items.ForEach(i => i.Checked = true);
    private void None_Click(object sender, RoutedEventArgs e) => _items.ForEach(i => i.Checked = false);
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>The ticked items, or null when cancelled. <paramref name="repoNames"/>: items are folder paths, shown as
    /// name and path columns.</summary>
    public static List<string>? Ask(IEnumerable<string> items, string? title = null, string? message = null, string? okText = null, bool repoNames = false)
    {
        var w = new ScanResultsWindow(items, repoNames);
        if (title is not null) w.Title = w.HeadingText.Text = title;
        if (message is not null) w.MessageText.Text = message;
        if (okText is not null) w.OkButton.Content = okText;
        return w.ShowDialog() == true ? w._items.Where(i => i.Checked).Select(i => i.Text).ToList() : null;
    }
}
