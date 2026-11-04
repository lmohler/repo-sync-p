using System.Windows.Input;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class ShortcutsTests
{
    [Fact]
    public void Table_has_unique_ids_and_no_key_clashes()
    {
        Assert.Equal(Shortcuts.All.Count, Shortcuts.All.Select(s => s.Id).Distinct().Count());
        Assert.Empty(Shortcuts.Clashes());
    }

    [Fact]
    public void Table_leaves_reserved_keys_alone()
    {
        // bound elsewhere (MainWindow.xaml, text boxes, Alt+digit tabs) - only the ids that own them may use them
        var reserved = new (Key, ModifierKeys)[]
        {
            (Key.C, ModifierKeys.Control), (Key.V, ModifierKeys.Control), (Key.X, ModifierKeys.Control), (Key.A, ModifierKeys.Control),
            (Key.Y, ModifierKeys.Control), (Key.D0, ModifierKeys.Control), (Key.D1, ModifierKeys.Control), (Key.D2, ModifierKeys.Control),
            (Key.D1, ModifierKeys.Alt), (Key.Enter, ModifierKeys.Control), (Key.Enter, ModifierKeys.Control | ModifierKeys.Shift),
            (Key.P, ModifierKeys.Control),
        };
        Assert.DoesNotContain(Shortcuts.All, s => reserved.Contains((s.Key, s.Modifiers)));
    }

    [Theory]
    [InlineData(Key.P, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+P")]
    [InlineData(Key.F5, ModifierKeys.None, "F5")]
    [InlineData(Key.OemComma, ModifierKeys.Control, "Ctrl+,")]
    [InlineData(Key.D3, ModifierKeys.Alt, "Alt+3")]
    [InlineData(Key.U, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+U")]
    public void Format_reads_like_a_menu(Key key, ModifierKeys mods, string expected) =>
        Assert.Equal(expected, Shortcuts.Format(key, mods));

    [Fact]
    public void WithShortcut_appends_once_and_falls_back_to_the_title()
    {
        Assert.Equal("Pull (Ctrl+Shift+P)", Shortcuts.WithShortcut("Pull", "pull"));
        Assert.Equal("Pull (Ctrl+Shift+P)", Shortcuts.WithShortcut("Pull (Ctrl+Shift+P)", "pull"));
        Assert.Equal("Refresh all (F5)", Shortcuts.WithShortcut(null, "refresh-all"));
        Assert.Equal("text", Shortcuts.WithShortcut("text", "no-such-id"));
    }
}

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("Pull & push", "pull")]
    [InlineData("Switch to feature/login", "swlogin")]
    [InlineData("Create merge request...", "cmr")]
    [InlineData("Open on the web", "OPEN WEB")]
    public void Matches_letters_in_order(string title, string query) => Assert.NotNull(FuzzyMatch.Score(title, "", query));

    [Theory]
    [InlineData("Push", "pull")]
    [InlineData("Fetch", "hcf")]
    public void Rejects_missing_or_out_of_order_letters(string title, string query) => Assert.Null(FuzzyMatch.Score(title, "", query));

    [Fact]
    public void Group_counts_when_the_title_misses()
    {
        Assert.NotNull(FuzzyMatch.Score("Fetch", "repo-sync", "fetch sync"));
        Assert.Null(FuzzyMatch.Score("Fetch", "repo-sync", "fetch nothing"));
    }

    [Fact]
    public void Prefix_and_word_start_rank_above_scattered_letters()
    {
        var items = new[] { "Open in editor", "Push", "Pull all", "Pull" };
        var ranked = FuzzyMatch.Filter(items, s => s, _ => "", "pu");
        Assert.Equal(new[] { "Push", "Pull all", "Pull" }, ranked); // ties keep the original order; "Open in editor" has no 'u'
        var byWord = FuzzyMatch.Filter(new[] { "Stage all", "Start a branch" }, s => s, _ => "", "sb");
        Assert.Equal("Start a branch", byWord[0]);
    }

    [Fact]
    public void Empty_query_keeps_everything_in_order()
    {
        var items = new[] { "b", "a" };
        Assert.Equal(items, FuzzyMatch.Filter(items, s => s, _ => "", "  "));
    }
}

public class GlobalSearchTests
{
    private static CommitInfo Commit(string subject, string author = "Ann Lee", string hash = "0123456789abcdef0123456789abcdef01234567") =>
        new() { Hash = hash, ShortHash = hash[..7], Subject = subject, Author = author, Email = "ann@example.com" };

    [Fact]
    public void Commit_matches_message_author_and_hash_prefix()
    {
        Assert.True(GlobalSearch.CommitMatches(Commit("Fix login timeout"), "login fix"));
        Assert.True(GlobalSearch.CommitMatches(Commit("Fix login timeout"), "ann"));
        Assert.True(GlobalSearch.CommitMatches(Commit("x"), "01234567"));
        Assert.False(GlobalSearch.CommitMatches(Commit("x"), "fedcba98")); // not this hash
        Assert.False(GlobalSearch.CommitMatches(Commit("Fix login"), "logout"));
    }

    [Fact]
    public void Merge_request_matches_reference_branches_and_title()
    {
        var mr = new ForgeMergeRequest(1, 42, "Add audit log", "opened", false, "https://x", false, "feature/audit", "main", null, null,
            new ForgeUser(1, "alee", "Ann Lee"), null, null, null, null, null);
        Assert.True(GlobalSearch.MergeRequestMatches(mr, "!42"));
        Assert.True(GlobalSearch.MergeRequestMatches(mr, "audit alee"));
        Assert.True(GlobalSearch.MergeRequestMatches(mr, "feature/audit"));
        Assert.False(GlobalSearch.MergeRequestMatches(mr, "billing"));
    }

    [Fact]
    public void Empty_query_matches_nothing() => Assert.False(GlobalSearch.Matches("  ", "anything"));

    [Fact]
    public async Task Finds_commits_and_branches_in_a_real_repo()
    {
        using var repo = await TempRepo.InitAsync();
        await repo.CommitAsync("a.txt", "one", "Add the widget parser");
        await repo.GitAsync("branch", "feature/widget-cache");
        var hits = await GlobalSearch.SearchAsync(new[] { new SearchRepo("demo", repo.Path, null, null) }, "widget", true, true, true);
        Assert.Contains(hits, h => h.Kind == HitKind.Commit && h.Title == "Add the widget parser" && h.Repo == "demo");
        Assert.Contains(hits, h => h.Kind == HitKind.Branch && h.Key == "feature/widget-cache");
        Assert.DoesNotContain(hits, h => h.Kind == HitKind.MergeRequest); // no remote, no server
    }
}
