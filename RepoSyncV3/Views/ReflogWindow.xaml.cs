using System.Windows;
using System.Windows.Input;
using RepoSyncV3.Models;

namespace RepoSyncV3.Views;

/// <summary>Picks a reflog entry. Result: <see cref="Picked"/> + <see cref="Action"/> ("reset" or "branch").</summary>
public partial class ReflogWindow : Window
{
    public ReflogEntry? Picked { get; private set; }
    public string Action { get; private set; } = string.Empty;

    public ReflogWindow(List<ReflogEntry> entries)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        Grid.ItemsSource = entries;
        if (entries.Count > 1) Grid.SelectedIndex = 1;
    }

    private void Finish(string action)
    {
        if (Grid.SelectedItem is not ReflogEntry e) return;
        Picked = e;
        Action = action;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Finish("reset");
    private void Branch_Click(object sender, RoutedEventArgs e) => Finish("branch");

    /// <summary>Closes with Action "history": the caller opens the Undo history window instead.</summary>
    private void History_Click(object sender, RoutedEventArgs e)
    {
        Action = "history";
        DialogResult = true;
    }
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Finish("reset");
}
