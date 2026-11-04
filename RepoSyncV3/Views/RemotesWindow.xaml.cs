using System.Windows;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Remotes (add / change URL / rename / remove) and submodules (list, init+update, add).
/// Every change goes through <c>run</c>, the detail view's logged git runner.</summary>
public partial class RemotesWindow : Window
{
    private sealed record Remote(string Name, string Url);
    private sealed record Submodule(string Path, string Commit, string State);

    private readonly string _path;
    private readonly Func<string, string[], bool, Task> _run;

    /// <param name="run">(title, git args, network).</param>
    public RemotesWindow(string path, Func<string, string[], bool, Task> run)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } w && w != this ? w : null;
        _path = path;
        _run = run;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var r = await Git.RunAsync(_path, new[] { "remote", "-v" });
        RemoteGrid.ItemsSource = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => new Remote(p[0], p[1]))
            .DistinctBy(x => x.Name)
            .ToList();

        var s = await Git.RunAsync(_path, new[] { "submodule", "status" });
        var subs = s.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Length > 1).Select(l =>
        {
            var state = l[0] switch { '-' => "not checked out", '+' => "on a different commit", 'U' => "conflict", _ => "up to date" };
            var p = l[1..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new Submodule(p.Length > 1 ? p[1] : "?", p.Length > 0 ? p[0][..Math.Min(8, p[0].Length)] : "", state);
        }).ToList();
        SubGrid.ItemsSource = subs;
        NoSubs.Visibility = subs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task Do(string title, bool network, params string[] args)
    {
        await _run(title, args, network);
        await LoadAsync();
    }

    private async void AddRemote_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask("Add remote", "Name (e.g. upstream):", "upstream");
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = PromptWindow.Ask("Add remote", $"URL for '{name.Trim()}':", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        await Do("Add remote", false, "remote", "add", name.Trim(), url.Trim());
    }

    private async void EditRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteGrid.SelectedItem is not Remote r) return;
        var url = PromptWindow.Ask("Change URL", $"New URL for '{r.Name}':", r.Url);
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == r.Url) return;
        await Do("Change remote URL", false, "remote", "set-url", r.Name, url.Trim());
    }

    private async void RenameRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteGrid.SelectedItem is not Remote r) return;
        var name = PromptWindow.Ask("Rename remote", $"New name for '{r.Name}':", r.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == r.Name) return;
        await Do("Rename remote", false, "remote", "rename", r.Name, name.Trim());
    }

    private async void RemoveRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteGrid.SelectedItem is not Remote r) return;
        if (!PromptWindow.Confirm("Remove remote", $"Remove remote '{r.Name}' ({r.Url})?\n\nNothing on the server is deleted; this PC just forgets it.", "Remove", danger: true)) return;
        await Do("Remove remote", false, "remote", "remove", r.Name);
    }

    private async void UpdateSubs_Click(object sender, RoutedEventArgs e) =>
        await Do("Update submodules", true, "submodule", "update", "--init", "--recursive");

    private async void RemoteSubs_Click(object sender, RoutedEventArgs e) =>
        await Do("Pull submodules", true, "submodule", "update", "--remote", "--recursive");

    private async void AddSub_Click(object sender, RoutedEventArgs e)
    {
        var url = PromptWindow.Ask("Add submodule", "Repository URL:", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        var guess = url.Trim().TrimEnd('/').Split('/').Last().Replace(".git", "");
        var folder = PromptWindow.Ask("Add submodule", "Folder inside this repo:", guess);
        if (string.IsNullOrWhiteSpace(folder)) return;
        await Do("Add submodule", true, "submodule", "add", url.Trim(), folder.Trim());
    }
}
