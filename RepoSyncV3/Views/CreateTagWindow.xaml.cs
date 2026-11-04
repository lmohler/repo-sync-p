using System.Windows;
using System.Windows.Controls;

namespace RepoSyncV3.Views;

public partial class CreateTagWindow : Window
{
    public string TagName => NameBox.Text.Trim();
    public string TargetCommit => TargetBox.Text.Trim();
    public string? TagMessage => string.IsNullOrWhiteSpace(MessageBox.Text) ? null : MessageBox.Text.Trim();
    public bool PushImmediately => PushBox.IsChecked == true;

    public CreateTagWindow(string repoName, string? defaultCommit = null)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        RepoText.Text = repoName;
        if (!string.IsNullOrWhiteSpace(defaultCommit))
            TargetBox.Text = defaultCommit.Trim();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
        };
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return;
        DialogResult = true;
    }
}

