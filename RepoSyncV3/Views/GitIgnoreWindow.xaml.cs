using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>View / edit the repo-root .gitignore. Keeps the file's existing line endings.</summary>
public partial class GitIgnoreWindow : Window
{
    private readonly string _file;
    private readonly string _editorCommand;
    private readonly string _newline;
    private string _original;

    public GitIgnoreWindow(string repoName, string repoPath, string editorCommand)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        _file = Path.Combine(repoPath, ".gitignore");
        _editorCommand = editorCommand;

        var exists = File.Exists(_file);
        var text = exists ? File.ReadAllText(_file) : string.Empty;
        _newline = text.Contains("\r\n") || !exists ? "\r\n" : "\n";
        _original = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Editor.Text = _original;
        PathText.Text = exists ? $"{repoName} - {_file}" : $"{repoName} - no .gitignore yet; saving creates {_file}";
        Title = $".gitignore - {repoName}";

        Loaded += (_, _) => Editor.Focus();
        Closing += (_, e) =>
        {
            if (DialogResult == true || !IsDirty) return;
            var answer = MessageBox.Show(this, "Discard your changes to .gitignore?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private bool IsDirty => Editor.Text != _original;

    /// <summary>Opens the dialog; true when the file was saved.</summary>
    public static bool Edit(string repoName, string repoPath, string editorCommand) =>
        new GitIgnoreWindow(repoName, repoPath, editorCommand).ShowDialog() == true;

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => SaveButton.IsEnabled = IsDirty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Editor.Text.Replace("\r\n", "\n");
            if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            File.WriteAllText(_file, text.Replace("\n", _newline), new UTF8Encoding(false));
            _original = Editor.Text;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save .gitignore:\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_file)) File.WriteAllText(_file, string.Empty, new UTF8Encoding(false));
        Shell.OpenInEditor(_editorCommand, _file, Path.GetDirectoryName(_file));
        DialogResult = false;
    }
}
