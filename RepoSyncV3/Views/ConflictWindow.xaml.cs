using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Resolve a conflicted file block by block: mine, theirs or both, with the result shown (and editable) below.</summary>
public partial class ConflictWindow : Window
{
    private readonly ConflictFile _file;
    private bool _edited;

    private ConflictWindow(string name, ConflictFile file)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        _file = file;
        HeadingText.Text = $"{name} - {file.Blocks.Count} conflict(s)";
        BlockList.ItemsSource = file.Blocks;
        ResultBox.Text = file.Render();
        ResultBox.TextChanged += (_, _) => _edited = true;
        Update();
    }

    private void Choice_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: ConflictBlock block, Tag: string tag } radio) return;
        if (_edited && !PromptWindow.Confirm("Replace your edits?", "You edited the result by hand. Picking a side rebuilds it from your choices.", "Rebuild"))
        {
            radio.IsChecked = false;
            return;
        }
        block.Choice = Enum.Parse<ConflictChoice>(tag);
        ResultBox.Text = _file.Render();
        _edited = false;
        Update();
    }

    private void Update()
    {
        var left = _file.Blocks.Count(b => !b.IsResolved);
        LeftText.Text = left == 0 ? "All conflicts resolved." : $"{left} conflict(s) still open - pick a side, or edit the result.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ResultBox.Text.Contains("<<<<<<<") || ResultBox.Text.Contains(">>>>>>>"))
        {
            if (!PromptWindow.Confirm("Conflict markers left", "The result still has <<<<<<< / >>>>>>> markers. Save it anyway?", "Save anyway")) return;
        }
        DialogResult = true;
    }

    /// <summary>The resolved text, or null when cancelled.</summary>
    public static string? Resolve(string name, ConflictFile file)
    {
        var w = new ConflictWindow(name, file);
        return w.ShowDialog() == true ? w.ResultBox.Text : null;
    }
}
