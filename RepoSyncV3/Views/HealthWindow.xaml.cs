using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Shows <see cref="RepoHealth"/> results. Result: <see cref="Action"/> = "cleanup", "gc" or empty.</summary>
public partial class HealthWindow : Window
{
    public string Action { get; private set; } = string.Empty;

    public HealthWindow(string repoName, List<HealthItem> items)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        Heading.Text = $"Health of {repoName}";
        Items.ItemsSource = items;
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        Action = "cleanup";
        DialogResult = true;
    }

    private void Gc_Click(object sender, RoutedEventArgs e)
    {
        Action = "gc";
        DialogResult = true;
    }
}
