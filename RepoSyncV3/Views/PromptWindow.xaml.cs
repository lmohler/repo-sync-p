using System.Windows;

namespace RepoSyncV3.Views;

/// <summary>Themed yes/no confirmation, or a one-line text prompt.</summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string message, string okText, bool danger)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (danger) OkButton.Style = (Style)FindResource("Button.Danger");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static bool Confirm(string title, string message, string okText = "OK", bool danger = false) =>
        new PromptWindow(title, message, okText, danger).ShowDialog() == true;

    /// <summary>The typed text, or null when cancelled.</summary>
    public static string? Ask(string title, string message, string initial, string okText = "OK")
    {
        var w = new PromptWindow(title, message, okText, false);
        w.InputBox.Visibility = Visibility.Visible;
        w.InputBox.Text = initial;
        w.Loaded += (_, _) =>
        {
            w.InputBox.Focus();
            w.InputBox.SelectAll();
        };
        return w.ShowDialog() == true ? w.InputBox.Text : null;
    }

    /// <summary>Asks which profile something goes into; the profile id, or null when cancelled.</summary>
    public static string? PickProfile(string title, string message, string? initial, string okText = "OK")
    {
        var w = new PromptWindow(title, message, okText, false);
        w.ChoiceBox.Visibility = Visibility.Visible;
        foreach (var p in Services.Workspaces.All) w.ChoiceBox.Items.Add(p);
        w.ChoiceBox.SelectedItem = Services.Workspaces.Get(initial);
        w.Loaded += (_, _) => w.ChoiceBox.Focus();
        return w.ShowDialog() == true ? (w.ChoiceBox.SelectedItem as Models.WorkspaceSettings)?.Id : null;
    }
}
