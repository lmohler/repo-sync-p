using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RepoSyncV3.Services;

namespace RepoSyncV3;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // an unhandled UI exception would otherwise close the app with no trace
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // crisp text in every window: pixel-snapped glyphs (Display) with ClearType, and layout on whole pixels
        TextOptions.TextFormattingModeProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata(TextFormattingMode.Display, FrameworkPropertyMetadataOptions.Inherits));
        TextOptions.TextRenderingModeProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata(TextRenderingMode.ClearType, FrameworkPropertyMetadataOptions.Inherits));
        UIElement.SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(true));
        FrameworkElement.UseLayoutRoundingProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
        // before the main window exists, so the first frame already has the right theme
        var settings = SettingsService.Load();
        ThemeService.Apply(ThemeService.FromSetting(settings.Theme));
        Density.Apply(settings.CompactDensity);
        // temp files from earlier sessions: TortoiseMerge copies, and rebase message folders nothing finished up
        _ = Task.Run(() =>
        {
            SvnCompare.CleanTemp(TimeSpan.FromHours(12));
            RepoActions.CleanOldRebaseTemp(TimeSpan.FromDays(30));
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsService.SettingsDir, "errors.log"),
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
        }
        catch
        {
            // best effort
        }
        MessageBox.Show($"An unexpected error occurred and was suppressed so the app could keep running:\n\n{e.Exception}",
            "Repo Sync v3", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
