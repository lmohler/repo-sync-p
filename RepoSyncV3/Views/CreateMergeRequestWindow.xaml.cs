using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.ViewModels;

namespace RepoSyncV3.Views;

public sealed record MergeRequestDraft(string Title, string Description, string Target, bool Draft, bool RemoveSource, bool Squash, bool OpenInBrowser);

/// <summary>New GitLab merge request or GitHub pull request for the current branch.</summary>
public partial class CreateMergeRequestWindow : Window
{
    private CreateMergeRequestWindow(RepoViewModel repo, IReadOnlyList<string> subjects, AppSettings settings, IEnumerable<string> targets)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsLoaded: true } owner && owner != this ? owner : null;
        var create = $"Create {repo.MrTerm}";
        Title = create;
        HeadingText.Text = $"{create} on {repo.ServerName}";
        OkButton.Content = create;
        BranchText.Text = $"{repo.Name}: {repo.Branch}  ({subjects.Count} commit(s))";
        if (repo.IsGitHubServer)
        {
            RemoveSourceBox.Visibility = Visibility.Collapsed;
            SquashBox.Visibility = Visibility.Collapsed;
            GitHubNote.Visibility = Visibility.Visible;
        }

        // one commit: its subject is the title; several: a title from the branch name and the subjects as a list
        TitleBox.Text = subjects.Count == 1 ? subjects[0] : TitleFromBranch(repo.Branch);
        var ticket = MrTemplates.TicketLink(settings.TicketUrl, Git.TicketFromBranch(repo.Branch));
        _generated = string.Join("\n", new[]
        {
            ticket is null ? null : $"Ticket: {ticket}",
            subjects.Count > 1 ? string.Join("\n", subjects.Select(s => "- " + s)) : null,
        }.Where(s => s is not null));
        DescriptionBox.Text = _generated;

        // the repo's templates; GitLab's "Default" is picked automatically
        _templates = MrTemplates.Find(repo.Path);
        TemplateBox.Items.Add("(none)");
        foreach (var t in _templates) TemplateBox.Items.Add(t.Name);
        TemplatePanel.Visibility = _templates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var preferred = _templates.FindIndex(t => t.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals("pull_request_template", StringComparison.OrdinalIgnoreCase));
        TemplateBox.SelectedIndex = preferred + 1;

        foreach (var t in targets) TargetBox.Items.Add(t);
        TargetBox.SelectedIndex = 0;
        RemoveSourceBox.IsChecked = settings.RemoveSourceBranch;
        SquashBox.IsChecked = settings.SquashOnMerge;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private readonly string _generated;
    private readonly List<MrTemplate> _templates;

    /// <summary>A template goes first, then the ticket link and commit list.</summary>
    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        var i = TemplateBox.SelectedIndex - 1;
        DescriptionBox.Text = i < 0 ? _generated : string.Join("\n\n", new[] { _templates[i].Text, _generated }.Where(s => s.Length > 0));
    }

    /// <summary>"feature/login-timeout-jdoe" -> "Login timeout jdoe".</summary>
    private static string TitleFromBranch(string branch)
    {
        var last = branch.Contains('/') ? branch[(branch.LastIndexOf('/') + 1)..] : branch;
        var words = last.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? branch : CultureInfo.CurrentCulture.TextInfo.ToUpper(words[0]) + words[1..];
    }

    private void Title_Changed(object sender, TextChangedEventArgs e)
    {
        if (OkButton is not null) OkButton.IsEnabled = TitleBox.Text.Trim().Length > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <param name="targets">Branches it can merge into, the default branch first.</param>
    public static MergeRequestDraft? Ask(RepoViewModel repo, IReadOnlyList<string> subjects, AppSettings settings, IReadOnlyList<string> targets)
    {
        var w = new CreateMergeRequestWindow(repo, subjects, settings, targets);
        if (w.ShowDialog() != true) return null;
        if (!repo.IsGitHubServer)
        {
            settings.RemoveSourceBranch = w.RemoveSourceBox.IsChecked == true;
            settings.SquashOnMerge = w.SquashBox.IsChecked == true;
        }
        return new MergeRequestDraft(w.TitleBox.Text.Trim(), w.DescriptionBox.Text, (string)w.TargetBox.SelectedItem, w.DraftBox.IsChecked == true,
            settings.RemoveSourceBranch, settings.SquashOnMerge, w.OpenBox.IsChecked == true);
    }
}
