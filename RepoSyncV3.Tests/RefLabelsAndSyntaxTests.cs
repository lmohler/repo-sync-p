using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class RefLabelsTests
{
    private static readonly string[] Remotes = { "origin" };

    [Fact]
    public void Parse_TypesEachRef_AndKeepsTheGraphColor()
    {
        // as Git.GetLogAsync hands it over: "HEAD -> " already removed
        var labels = RefLabels.Parse("main, origin/main, origin/HEAD, feature/x, tag: v1.2", "main", Remotes, 3);

        Assert.Equal(new[] { "main", "feature/x", "origin/main", "v1.2" }, labels.Select(l => l.Name));
        Assert.Equal(new[] { RefKind.Head, RefKind.Local, RefKind.Remote, RefKind.Tag }, labels.Select(l => l.Kind));
        Assert.All(labels, l => Assert.Equal(3, l.Color));
    }

    [Fact]
    public void Parse_RawDecoration_AndDetachedHead()
    {
        var labels = RefLabels.Parse("HEAD -> dev, upstream/dev", null, new[] { "upstream" }, 0);
        Assert.Equal(RefKind.Head, labels[0].Kind);
        Assert.Equal("dev", labels[0].Name);
        Assert.Equal(RefKind.Remote, labels[1].Kind);

        var detached = RefLabels.Parse("HEAD", null, Remotes, 0).Single();
        Assert.True(detached.IsHead);
        Assert.False(detached.IsBranch);
    }

    [Fact]
    public void Parse_Empty_IsNoLabels_AndTagsAreNotBranches()
    {
        Assert.Empty(RefLabels.Parse("", "main", Remotes, 0));
        Assert.False(RefLabels.Parse("tag: v1", "main", Remotes, 0).Single().IsBranch);
    }
}

public sealed class WordDiffAndSyntaxTests
{
    private static string Cut(string s, (int Start, int Length) span) => s.Substring(span.Start, span.Length);

    [Fact]
    public void WordDiff_MarksEachChangedWord_NotTheWholeMiddle()
    {
        var (a, b) = DiffDecor.WordDiff("call(alpha, keep, beta);", "call(gamma, keep, delta);");
        Assert.Equal(new[] { "alpha", "beta" }, a!.Select(s => Cut("call(alpha, keep, beta);", s)));
        Assert.Equal(new[] { "gamma", "delta" }, b!.Select(s => Cut("call(gamma, keep, delta);", s)));
    }

    [Fact]
    public void WordDiff_PureInsert_OnlyMarksTheNewSide()
    {
        var (a, b) = DiffDecor.WordDiff("int x = 1;", "int x = 1 + y;");
        Assert.Empty(a!);
        Assert.Equal("+ y", Cut("int x = 1 + y;", b!.Single()));
    }

    [Fact]
    public void Mark_SetsSpansOnPairedLines_WithPrefixOffset()
    {
        var lines = Git.ParseDiff("+++ b/a.cs\n@@ -1 +1 @@\n-foo(one, two, three, four);\n+foo(uno, two, three, quattro);\n");
        var add = lines.Single(l => l.Kind == DiffKind.Add);
        Assert.Equal(new[] { "uno", "quattro" }, add.ChangedSpans!.Select(s => Cut(add.Text, s)));
        var segs = DiffDecor.Segments(add);
        Assert.Equal(add.Text, string.Concat(segs.Select(s => s.Text)));
        Assert.Equal(new[] { "uno", "quattro" }, segs.Where(s => s.Changed).Select(s => s.Text));
    }

    private static List<(string Text, TokenKind Kind)> Tokens(string line, string ext) =>
        DiffDecor.Tokenize(line, 0, DiffDecor.LanguageOf(ext)).Where(t => t.Kind != TokenKind.Plain)
            .Select(t => (line.Substring(t.Start, t.Length), t.Kind)).ToList();

    [Theory]
    [InlineData(".cs", "return \"hi\"; // done", "return", "\"hi\"", "// done")]
    [InlineData(".ts", "const s = 'x'; /* note */", "const", "'x'", "/* note */")]
    [InlineData(".ps1", "if ($a -eq 'b') { } # check", "if", "'b'", "# check")]
    [InlineData(".py", "def f(): return \"s\"  # c", "def", "\"s\"", "# c")]
    [InlineData(".sql", "SELECT name FROM t WHERE x = 'y' -- c", "SELECT", "'y'", "-- c")]
    public void Tokenize_FindsKeywordStringAndComment(string ext, string line, string keyword, string str, string comment)
    {
        var tokens = Tokens(line, ext);
        Assert.Contains((keyword, TokenKind.Keyword), tokens);
        Assert.Contains((str, TokenKind.String), tokens);
        Assert.Contains((comment, TokenKind.Comment), tokens);
    }

    [Fact]
    public void Tokenize_PowerShellOperators_AreKeywords()
    {
        Assert.Contains(("-eq", TokenKind.Keyword), Tokens("if ($a -eq 1) {}", ".ps1"));
    }

    [Fact]
    public void Tokenize_Xml_TagsAttributesAndComments()
    {
        var tokens = Tokens("<Button Content=\"Go\"/> <!-- hi -->", ".xaml");
        Assert.Contains(("Button", TokenKind.Keyword), tokens);
        Assert.Contains(("\"Go\"", TokenKind.String), tokens);
        Assert.Contains(("<!-- hi -->", TokenKind.Comment), tokens);
        Assert.DoesNotContain(tokens, t => t.Text == "Content");
    }

    [Fact]
    public void Tokenize_Json_StringsAndLiterals()
    {
        var tokens = Tokens("\"a\": true, \"b\": 12", ".json");
        Assert.Contains(("\"a\"", TokenKind.String), tokens);
        Assert.Contains(("true", TokenKind.Keyword), tokens);
        Assert.Contains(("12", TokenKind.Number), tokens);
    }

    [Fact]
    public void NextChange_JumpsBetweenBlockStarts()
    {
        //                 0      1     2     3      4      5     6
        var flags = new[] { false, true, true, false, false, true, true };
        Assert.Equal(1, DiffDecor.NextChange(flags, -1, true));
        Assert.Equal(5, DiffDecor.NextChange(flags, 1, true));
        Assert.Equal(-1, DiffDecor.NextChange(flags, 5, true));
        Assert.Equal(1, DiffDecor.NextChange(flags, 6, false));
        Assert.Equal(5, DiffDecor.NextChange(flags, 7, false));
        Assert.Equal(-1, DiffDecor.NextChange(flags, 1, false));
    }
}
