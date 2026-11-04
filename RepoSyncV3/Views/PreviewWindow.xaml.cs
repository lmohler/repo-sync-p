using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Confirm dialog for history-rewriting actions: shows a <see cref="RewritePreview"/> (commits rewritten
/// or dropped, uncommitted files lost, server commits overwritten) before anything runs.</summary>
public partial class PreviewWindow : Window
{
    private PreviewWindow(RewritePreview preview, string okText, string? extra)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        HeadingText.Text = preview.Title;
        SummaryText.Text = string.IsNullOrEmpty(extra) ? preview.Summary : preview.Summary + "\n\n" + extra;
        SectionList.ItemsSource = preview.Sections;
        OkButton.Content = okText;
        if (preview.Danger) OkButton.Style = (Style)FindResource("Button.Danger");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <param name="extra">An extra paragraph under the summary (e.g. what the action does next).</param>
    public static bool Confirm(RewritePreview preview, string okText, string? extra = null) =>
        new PreviewWindow(preview, okText, extra).ShowDialog() == true;
}
