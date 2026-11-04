using System.Windows;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

public enum IdentityChoice
{
    Cancel,
    UseWorkspace,
    MoveWorkspace,
    Proceed,
}

/// <summary>Stops a commit / push whose author email doesn't belong to the repo's profile, and asks what to do.</summary>
public partial class IdentityWindow : Window
{
    private IdentityChoice _choice = IdentityChoice.Cancel;

    /// <summary>The profile whose identity the repo already commits as (offered as "move it there"); null = none.</summary>
    private readonly WorkspaceSettings? _other;

    private IdentityWindow(RepoViewModel repo, string what)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        var w = repo.Workspace;
        _other = Workspaces.All.FirstOrDefault(p => p.Id != w.Id && p.GitEmail.Length > 0
                                                    && string.Equals(p.GitEmail, repo.GitEmail, StringComparison.OrdinalIgnoreCase));

        HeadingText.Text = $"{repo.Name} is a {w.Name} repo, but your git identity here isn't";
        MessageText.Text = $"Before you {what}: commits record the author's email for good, and pushed commits can't be changed without rewriting history.";
        CurrentText.Text = repo.GitEmail.Length == 0 ? "(no email set)" : $"{repo.GitName} <{repo.GitEmail}>";
        ExpectedLabel.Text = w.Name.ToUpperInvariant() + " USES";
        ExpectedText.Text = $"{(w.GitName.Length > 0 ? w.GitName : repo.GitName)} <{w.GitEmail}>";

        UseButton.Content = $"Use {w.GitEmail} for this repo, then {what}";
        if (_other is null) MoveButton.Visibility = Visibility.Collapsed;
        else MoveButton.Content = $"This is really a {_other.Name} repo - move it to {_other.Name}, then {what}";
        ProceedButton.Content = $"{char.ToUpperInvariant(what[0])}{what[1..]} as {(repo.GitEmail.Length == 0 ? "is" : repo.GitEmail)} anyway";
        Loaded += (_, _) => UseButton.Focus();
    }

    private void Use_Click(object sender, RoutedEventArgs e) => Close(IdentityChoice.UseWorkspace);
    private void Move_Click(object sender, RoutedEventArgs e) => Close(IdentityChoice.MoveWorkspace);

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        if (PromptWindow.Confirm("Go ahead with this identity?", "The commits will carry this email permanently. Continue?", "Continue", danger: true))
            Close(IdentityChoice.Proceed);
    }

    private void Close(IdentityChoice choice)
    {
        _choice = choice;
        DialogResult = true;
    }

    /// <param name="what">"commit", "push", "commit and push".</param>
    /// <param name="moveTo">With <see cref="IdentityChoice.MoveWorkspace"/>: the profile to move the repo to.</param>
    public static IdentityChoice Ask(RepoViewModel repo, string what, out string? moveTo)
    {
        var w = new IdentityWindow(repo, what);
        var choice = w.ShowDialog() == true ? w._choice : IdentityChoice.Cancel;
        moveTo = w._other?.Id;
        return choice;
    }
}
