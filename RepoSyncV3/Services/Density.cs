using System.Windows;

namespace RepoSyncV3.Services;

/// <summary>Normal or compact spacing. The Density.* resources (defaults in Themes/Controls.xaml) are overridden at
/// application level, so every DynamicResource use updates at once - like the theme swap.</summary>
public static class Density
{
    public static void Apply(bool compact)
    {
        var r = Application.Current.Resources;
        r["Density.ItemPadding"] = compact ? new Thickness(5, 1, 5, 1) : new Thickness(7, 3, 7, 3);
        r["Density.RowHeight"] = compact ? 20.0 : 24.0;
        r["Density.CardMargin"] = compact ? new Thickness(0, 0, 2, 2) : new Thickness(0, 0, 2, 4);
        r["Density.CardContent"] = compact ? new Thickness(9, 3, 8, 2) : new Thickness(9, 6, 8, 6);
        r["Density.Avatar"] = compact ? 24.0 : 30.0;
        r["Density.AvatarMargin"] = compact ? new Thickness(8, 4, 0, 0) : new Thickness(8, 7, 0, 0);
    }
}
