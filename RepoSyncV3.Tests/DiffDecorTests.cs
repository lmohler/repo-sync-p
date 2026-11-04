using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class DiffDecorTests
{
    private const string Diff =
        "diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1,2 +1,2 @@\n" +
        " var x = 1;\n-var count = Total(items);\n+var count = Sum(items);\n";

    [Fact]
    public void PairedLines_HighlightOnlyTheChangedWord()
    {
        var lines = Git.ParseDiff(Diff);
        var del = lines.Single(l => l.Kind == DiffKind.Delete);
        var add = lines.Single(l => l.Kind == DiffKind.Add);

        Assert.Equal("Total", del.Text.Substring(del.Changed!.Value.Start, del.Changed.Value.Length));
        Assert.Equal("Sum", add.Text.Substring(add.Changed!.Value.Start, add.Changed.Value.Length));
        Assert.Equal(".cs", add.Ext);
    }

    [Fact]
    public void Segments_ColourKeywordsAndKeepAllText()
    {
        var add = Git.ParseDiff(Diff).Single(l => l.Kind == DiffKind.Add);
        var segs = DiffDecor.Segments(add);

        Assert.Equal(add.Text, string.Concat(segs.Select(s => s.Text)));
        Assert.Contains(segs, s => s.Text == "var" && s.Token == TokenKind.Keyword);
        Assert.Contains(segs, s => s.Text == "Sum" && s.Changed);
    }

    [Fact]
    public void WholeLineRewrite_HasNoWordHighlight()
    {
        var lines = Git.ParseDiff("+++ b/x.txt\n@@ -1 +1 @@\n-alpha beta gamma\n+something else entirely\n");
        Assert.All(lines.Where(l => l.IsChange), l => Assert.Null(l.Changed));
    }
}
