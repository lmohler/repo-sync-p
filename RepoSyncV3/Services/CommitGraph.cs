using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>One history row of the branch graph: where the dot sits and the line pieces to draw.
/// Y is 0 (top of the row), 0.5 (the dot) or 1 (bottom); X is a lane number.
/// Colors are per branch line, not per lane, so a line keeps its color when it shifts or a lane is reused.</summary>
public sealed class GraphRow
{
    public int Lane { get; init; }
    public int Width { get; init; }
    public int DotColor { get; init; }
    public bool IsMerge { get; init; }
    public List<(int X1, double Y1, int X2, double Y2)> Segments { get; } = new();
    /// <summary>Color index of each entry in <see cref="Segments"/>.</summary>
    public List<int> SegmentColors { get; } = new();

    internal void Add(int x1, double y1, int x2, double y2, int color)
    {
        Segments.Add((x1, y1, x2, y2));
        SegmentColors.Add(color);
    }
}

/// <summary>Lays out lanes for commits in log order (newest first), like `git log --graph` but one dot per row.</summary>
public static class CommitGraph
{
    public static void Layout(IReadOnlyList<CommitInfo> commits)
    {
        var lanes = new List<string?>();
        var colors = new List<int>();
        var nextColor = 0;

        int FreeLane()
        {
            var k = lanes.IndexOf(null);
            if (k >= 0) return k;
            lanes.Add(null);
            colors.Add(0);
            return lanes.Count - 1;
        }

        foreach (var c in commits)
        {
            var before = lanes.ToList();
            var beforeColors = colors.ToList();
            var lane = lanes.IndexOf(c.Hash);
            if (lane < 0)
            {
                // a new branch tip: a line of its own color
                lane = FreeLane();
                colors[lane] = nextColor++;
            }
            var dotColor = colors[lane];

            // other lanes that were also waiting for this commit end here
            for (var j = 0; j < lanes.Count; j++)
                if (j != lane && lanes[j] == c.Hash) lanes[j] = null;

            var targets = new List<int>();
            lanes[lane] = c.Parents.Length > 0 ? c.Parents[0] : null;
            if (c.Parents.Length > 0) targets.Add(lane);
            foreach (var p in c.Parents.Skip(1))
            {
                var k = lanes.IndexOf(p);
                if (k < 0)
                {
                    k = FreeLane();
                    lanes[k] = p;
                    colors[k] = nextColor++;
                }
                targets.Add(k);
            }

            var row = new GraphRow
            {
                Lane = lane,
                Width = Math.Max(before.Count, lanes.Count),
                DotColor = dotColor,
                IsMerge = c.Parents.Length > 1,
            };
            for (var j = 0; j < before.Count; j++)
            {
                if (before[j] is null) continue;
                if (before[j] == c.Hash) row.Add(j, 0, lane, 0.5, beforeColors[j]);
                else if (j < lanes.Count && lanes[j] == before[j]) row.Add(j, 0, j, 1, beforeColors[j]);
            }
            foreach (var k in targets) row.Add(lane, 0.5, k, 1, colors[k]);
            c.Graph = row;

            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }
        }
    }
}
