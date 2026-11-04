using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

public class CommitGraphTests
{
    private static CommitInfo C(string hash, params string[] parents) => new() { Hash = hash, ShortHash = hash, Parents = parents };

    [Fact]
    public void Linear_StaysInLaneZero()
    {
        var list = new[] { C("c", "b"), C("b", "a"), C("a") };
        CommitGraph.Layout(list);
        Assert.All(list, c => Assert.Equal(0, c.Graph!.Lane));
    }

    [Fact]
    public void Merge_OpensSecondLane_AndClosesIt()
    {
        // m merges f (feature) into b; f branched from a
        var list = new[] { C("m", "b", "f"), C("f", "a"), C("b", "a"), C("a") };
        CommitGraph.Layout(list);
        Assert.Equal(0, list[0].Graph!.Lane);
        Assert.Contains(list[0].Graph!.Segments, s => s is (0, 0.5, 1, 1));
        Assert.Equal(1, list[1].Graph!.Lane);
        Assert.Equal(0, list[2].Graph!.Lane);
        Assert.Equal(0, list[3].Graph!.Lane);
        // both lanes come down into a
        Assert.Equal(2, list[3].Graph!.Segments.Count(s => s.Y2 == 0.5));
    }
}
