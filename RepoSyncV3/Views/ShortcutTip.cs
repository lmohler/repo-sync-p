using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>
/// views:ShortcutTip.Id="pull" on a button appends its key from the Shortcuts table to the tooltip
/// ("Bring in the server's new commits (Ctrl+Shift+P)"), or uses the shortcut's title when there is no tooltip.
/// Done on Loaded so the element's own ToolTip attribute (parsed in any order) is already set.
/// </summary>
public static class ShortcutTip
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(ShortcutTip), new PropertyMetadata(null, OnIdChanged));

    public static string? GetId(DependencyObject d) => (string?)d.GetValue(IdProperty);
    public static void SetId(DependencyObject d, string? value) => d.SetValue(IdProperty, value);

    private static void OnIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= Element_Loaded;
        fe.Loaded += Element_Loaded;
        if (fe.IsLoaded) Apply(fe);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        if (GetId(fe) is not { } id) return;
        if (fe.ToolTip is null or string) fe.ToolTip = Shortcuts.WithShortcut(fe.ToolTip as string, id);
    }
}
