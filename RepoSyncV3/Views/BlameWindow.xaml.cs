using System.Windows;
using RepoSyncV3.Models;

namespace RepoSyncV3.Views;

public partial class BlameWindow : Window
{
    public BlameWindow(string relativePath, List<BlameLine> lines)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        FileText.Text = relativePath;
        BlameGrid.ItemsSource = lines;
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (BlameGrid.SelectedItem is BlameLine line && !string.IsNullOrWhiteSpace(line.CommitHash))
        {
            Clipboard.SetText(line.CommitHash);
        }
    }
}

