using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>settings.json (and gitlab-tokens.json) live next to the exe, so the whole app folder is portable.</summary>
public static class SettingsService
{
    public static readonly string SettingsDir = AppContext.BaseDirectory;

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");
    private static readonly string BackupFile = SettingsFile + ".bak";

    /// <summary>Set when Load could not read settings.json; MainViewModel shows it in the Activity panel.</summary>
    public static string? LoadProblem { get; private set; }

    /// <summary>
    /// settings.json, else the .bak from the previous save, else defaults. A file that won't parse is copied
    /// aside (settings.json.corrupt-yyyyMMdd-HHmmss) before anything can overwrite it, so the repo list is never lost.
    /// </summary>
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFile)) return TryRead(BackupFile) ?? new AppSettings();
        if (TryRead(SettingsFile) is { } settings) return settings;

        if (LoadProblem is null)
        {
            var aside = $"{SettingsFile}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(SettingsFile, aside, overwrite: true); } catch { aside = SettingsFile; }
            var fromBackup = TryRead(BackupFile) is not null;
            LoadProblem = $"settings.json could not be read and was kept as {Path.GetFileName(aside)}. " +
                          (fromBackup ? "Loaded the previous save (settings.json.bak) instead." : "Started with default settings.");
        }
        return TryRead(BackupFile) ?? new AppSettings();
    }

    private static AppSettings? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a temp file and swaps it in (the old file becomes .bak), so a crash mid-save can't corrupt settings.json.</summary>
    /// <returns>null on success, else why it failed.</returns>
    public static string? Save(AppSettings settings)
    {
        var temp = SettingsFile + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(SettingsFile)) File.Replace(temp, SettingsFile, BackupFile, ignoreMetadataErrors: true);
            else File.Move(temp, SettingsFile);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            return $"Could not save settings.json: {ex.Message}";
        }
    }
}

public enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>Swaps the active Themes/Colors.*.xaml dictionary; every DynamicResource brush updates at once.</summary>
public static class ThemeService
{
    public static ThemeMode Current { get; private set; } = ThemeMode.Light;

    public static void Apply(ThemeMode mode)
    {
        Current = mode;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source is not null && d.Source.OriginalString.Contains("Themes/Colors."));
        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Insert(0, new ResourceDictionary { Source = new Uri($"Themes/Colors.{mode}.xaml", UriKind.Relative) });
    }

    public static ThemeMode FromSetting(string? value) =>
        string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark
        : string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Light
        : DetectSystemTheme();

    public static ThemeMode DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch
        {
            // default to light
        }
        return ThemeMode.Light;
    }
}

/// <summary>Opening things outside the app: browser, Explorer, your editor.</summary>
public static class Shell
{
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no default browser */ }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { /* best effort */ }
    }

    public static void SelectInExplorer(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); } catch { /* best effort */ }
    }

    /// <summary>Runs the editor command (e.g. "code") with the path; through cmd so .cmd shims like code.cmd resolve.</summary>
    public static void OpenInEditor(string editorCommand, string path, string? workingDir = null)
    {
        if (string.IsNullOrWhiteSpace(editorCommand)) editorCommand = "code";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {editorCommand} \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            });
        }
        catch
        {
            OpenFolder(path);
        }
    }

    /// <summary>Opens a file at a line: VS Code (any "code" command) gets -g file:line, other editors just the file.</summary>
    public static void OpenInEditorAt(string editorCommand, string path, int line, string? workingDir = null)
    {
        if (string.IsNullOrWhiteSpace(editorCommand)) editorCommand = "code";
        var isCode = Path.GetFileNameWithoutExtension(editorCommand.Trim().Trim('"').Split(' ')[0]).StartsWith("code", StringComparison.OrdinalIgnoreCase);
        if (isCode) OpenInEditor(editorCommand + " -g", $"{path}:{line}", workingDir);
        else OpenInEditor(editorCommand, path, workingDir);
    }

    /// <summary>Opens Windows Terminal or PowerShell in the given folder.</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder });
            }
            catch
            {
                OpenFolder(folder);
            }
        }
    }
}
