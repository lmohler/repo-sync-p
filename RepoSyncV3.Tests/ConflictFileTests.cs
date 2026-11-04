using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public sealed class ConflictFileTests
{
    private const string Text =
        "top\r\n<<<<<<< HEAD\r\nmine 1\r\n=======\r\ntheirs 1\r\n>>>>>>> feature\r\nmiddle\r\n" +
        "<<<<<<< HEAD\r\nmine 2\r\n||||||| base\r\nbase 2\r\n=======\r\ntheirs 2\r\n>>>>>>> feature\r\nend";

    [Fact]
    public void Parse_FindsBlocks_SkipsDiff3Base()
    {
        var f = ConflictFile.Parse(Text, rebase: false);
        Assert.Equal(2, f.Blocks.Count);
        Assert.Equal("mine 2", f.Blocks[1].MineText);
        Assert.Equal("theirs 2", f.Blocks[1].TheirsText);
    }

    [Fact]
    public void Render_UnresolvedIsUnchanged_ChoicesKeepCrlf()
    {
        var f = ConflictFile.Parse(Text, rebase: false);
        Assert.Equal(Text, f.Render());
        Assert.False(f.AllResolved);

        f.Blocks[0].Choice = ConflictChoice.Theirs;
        f.Blocks[1].Choice = ConflictChoice.MineThenTheirs;
        Assert.True(f.AllResolved);
        Assert.Equal("top\r\ntheirs 1\r\nmiddle\r\nmine 2\r\ntheirs 2\r\nend", f.Render());
    }

    [Fact]
    public void Rebase_SwapsSides()
    {
        var f = ConflictFile.Parse(Text, rebase: true);
        Assert.Equal("theirs 1", f.Blocks[0].MineText);
    }
}
