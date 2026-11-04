using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Picks the folder or file row template for a file tree ListBox.</summary>
public sealed class FileTreeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is FileTreeFolder ? FolderTemplate : FileTemplate;
}

/// <summary>Folding folder rows from the keyboard (Left / Right, Enter) and by double-click.</summary>
public static class FileTreeInput
{
    /// <summary>True when the selected row is a folder (it was toggled, so the caller skips its file action).</summary>
    public static bool ToggleSelectedFolder(ListBox list)
    {
        if (list.SelectedItem is not FileTreeFolder folder) return false;
        Refocus(list, folder.Path, () => folder.IsExpanded = !folder.IsExpanded);
        return true;
    }

    public static void PreviewKeyDown(ListBox list, KeyEventArgs e)
    {
        if (list.SelectedItem is not FileTreeFolder folder || Keyboard.Modifiers != ModifierKeys.None) return;
        bool? expand = e.Key switch { Key.Left => false, Key.Right => true, Key.Enter or Key.Space => !folder.IsExpanded, _ => null };
        if (expand is not { } x) return;
        e.Handled = true;
        if (folder.IsExpanded != x) Refocus(list, folder.Path, () => folder.IsExpanded = x);
    }

    /// <summary>Folding rebuilds the rows, so select the (new) folder row again to keep the keyboard on it.</summary>
    private static void Refocus(ListBox list, string path, Action toggle)
    {
        toggle();
        var again = list.Items.OfType<FileTreeFolder>().FirstOrDefault(f => f.Path == path);
        if (again is null) return;
        list.SelectedItem = again;
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(again) as ListBoxItem)?.Focus();
    }
}
