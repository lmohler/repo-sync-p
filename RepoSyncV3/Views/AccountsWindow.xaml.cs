using System.Windows;
using RepoSyncV3.Models;
using RepoSyncV3.Services;

namespace RepoSyncV3.Views;

/// <summary>Saves and removes the personal access token for each GitHub / GitLab server.</summary>
public partial class AccountsWindow : Window
{
    // public: WPF bindings only see properties of public types
    public sealed record SavedAccount(string Host, string Kind);

    private AccountsWindow(ForgeRemote? remote)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        var gitLabHost = remote is { IsGitHub: false } ? remote.Host
            : TokenStore.Hosts.FirstOrDefault(h => !ForgeLinks.IsGitHubHost(h)) ?? "gitlab.example.com";
        HostBox.Text = gitLabHost;
        if (remote?.IsGitHub == true || (remote is null && !TokenStore.Has("github.com"))) GitHubRadio.IsChecked = true;
        else GitLabRadio.IsChecked = true;
        RefreshSaved();
        Loaded += (_, _) => TokenBox.Focus();
    }

    private ForgeKind Kind => GitHubRadio.IsChecked == true ? ForgeKind.GitHub : ForgeKind.GitLab;

    private string BaseUrl => Kind == ForgeKind.GitHub
        ? "https://github.com"
        : "https://" + HostBox.Text.Trim().TrimEnd('/').Replace("https://", "").Replace("http://", "");

    private string Host => BaseUrl["https://".Length..];

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (HostBox is null) return;
        var github = Kind == ForgeKind.GitHub;
        HostBox.Visibility = github ? Visibility.Collapsed : Visibility.Visible;
        CreateTokenText.Text = github
            ? "Create a token on GitHub (opens the page with the 'repo', 'workflow' and 'read:org' scopes ticked)"
            : "Create a token on GitLab (opens the page with the 'api' scope ticked)";
    }

    private void RefreshSaved()
    {
        var accounts = TokenStore.Hosts.Select(h => new SavedAccount(h, ForgeLinks.IsGitHubHost(h) ? "GitHub" : "GitLab")).ToList();
        SavedList.ItemsSource = accounts;
        NoneText.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowResult(string text, string tone)
    {
        ResultText.Text = text;
        ResultBorder.Tag = tone;
        ResultBorder.Visibility = Visibility.Visible;
    }

    private void CreateToken_Click(object sender, RoutedEventArgs e)
    {
        if (Kind == ForgeKind.GitHub || HostBox.Text.Trim().Length > 0) Shell.OpenUrl(ForgeLinks.NewToken(Kind, BaseUrl));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if ((Kind == ForgeKind.GitLab && HostBox.Text.Trim().Length == 0) || token.Length == 0)
        {
            ShowResult(Kind == ForgeKind.GitLab ? "Enter the server and the token." : "Paste the token.", "warning");
            return;
        }
        SaveButton.IsEnabled = false;
        ShowResult("Checking the token...", "info");
        try
        {
            var user = await Forges.Create(Kind, Host, token, BaseUrl).GetCurrentUserUncachedAsync();
            TokenStore.Set(Host, token);
            TokenBox.Clear();
            ShowResult($"Saved. Signed in to {Host} as {user.Name} (@{user.Username}).", "success");
            RefreshSaved();
        }
        catch (UserFacingException ex)
        {
            ShowResult(ex.Message + (ex.Help.Count > 0 ? "\n" + string.Join("\n", ex.Help) : string.Empty), "error");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string host) return;
        if (!PromptWindow.Confirm("Remove token", $"Forget the token for {host}?", "Remove", danger: true)) return;
        TokenStore.Remove(host);
        RefreshSaved();
    }

    public static void Open(ForgeRemote? remote) => new AccountsWindow(remote).ShowDialog();
}
