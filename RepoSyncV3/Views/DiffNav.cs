using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>
/// <c>v:DiffNav.IsEnabled="True"</c> on a diff ListBox: F7 / Alt+Down jumps to the next block of changed lines,
/// Shift+F7 / Alt+Up to the previous one. Works on unified (<see cref="DiffLine"/>) and split (<see cref="SideBySideDiffRow"/>) lists;
/// set by the DiffList style and the split view's panes, so every diff gets it.
/// </summary>
public static class DiffNav
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DiffNav), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        list.PreviewKeyDown -= List_PreviewKeyDown;
        if ((bool)e.NewValue) list.PreviewKeyDown += List_PreviewKeyDown;
    }

    private static void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = (ListBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool? forward = (key, mods) switch
        {
            (Key.F7, ModifierKeys.None) or (Key.Down, ModifierKeys.Alt) => true,
            (Key.F7, ModifierKeys.Shift) or (Key.Up, ModifierKeys.Alt) => false,
            _ => null,
        };
        if (forward is null) return;
        e.Handled = true;
        Jump(list, forward.Value);
    }

    /// <summary>Selects and scrolls to the next / previous change; returns false when there isn't one.</summary>
    public static bool Jump(ListBox list, bool forward)
    {
        var flags = list.Items.Cast<object>().Select(IsChange).ToList();
        var target = DiffDecor.NextChange(flags, list.SelectedIndex, forward);
        if (target < 0) return false;
        if (list.SelectionMode != SelectionMode.Single) list.SelectedItems.Clear();
        list.SelectedIndex = target;
        // show a little context above the hunk, not glued to the top edge
        list.ScrollIntoView(list.Items[Math.Min(flags.Count - 1, target + 3)]);
        list.ScrollIntoView(list.Items[Math.Max(0, target - 2)]);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromIndex(target) as ListBoxItem)?.Focus();
        return true;
    }

    private static bool IsChange(object item) => item switch
    {
        DiffLine l => l.IsChange,
        SideBySideDiffRow r => r.LeftLine?.IsChange == true || r.RightLine?.IsChange == true,
        _ => false,
    };
}
