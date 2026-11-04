using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>svn log parsing, revision -> svn-rNNNN mirror mapping, and the workflow's step progress reports.</summary>
public sealed class SvnLogTests
{
    private const string LogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <log>
        <logentry revision="12">
        <author>jdoe</author>
        <date>2026-10-29T14:05:00.123456Z</date>
        <paths>
        <path action="M" kind="file">/trunk/a.txt</path>
        <path action="A" kind="file" copyfrom-path="/trunk/b.txt" copyfrom-rev="10">/trunk/c.txt</path>
        </paths>
        <msg>PROJ-77 Fix totals
        second line</msg>
        </logentry>
        <logentry revision="11">
        <date>2026-10-28T09:00:00.000000Z</date>
        <msg></msg>
        </logentry>
        </log>
        """;

    [Fact]
    public void ParseLogXml_ReadsRevisionsAuthorsPathsAndMessages()
    {
        var revisions = SvnLog.ParseLogXml(LogXml);

        Assert.Equal(new long[] { 12, 11 }, revisions.Select(r => r.Revision));
        var r12 = revisions[0];
        Assert.Equal("jdoe", r12.Author);
        Assert.Equal("PROJ-77 Fix totals", r12.Subject);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 14, 5, 0, TimeSpan.Zero).AddTicks(1_234_560).LocalDateTime, r12.Date);
        Assert.Equal(2, r12.Paths.Count);
        Assert.Equal('M', r12.Paths[0].Action);
        Assert.Equal("/trunk/a.txt", r12.Paths[0].Path);
        Assert.Equal("/trunk/b.txt@10", r12.Paths[1].CopyFrom);
        Assert.Equal(string.Empty, revisions[1].Author);
        Assert.Empty(revisions[1].Paths);
        Assert.Empty(SvnLog.ParseLogXml(string.Empty));
    }

    [Fact]
    public void ParseMirrorTags_UsesPeeledCommitForAnnotatedTags()
    {
        var tags = SvnLog.ParseMirrorTags("svn-r5 aaa111 ccc333\nsvn-r12 bbb222 \nsvn-rX 000 000\nother 1 2\n");

        Assert.Equal(new long[] { 5, 12 }, tags.Keys);
        Assert.Equal("ccc333", tags[5]);
        Assert.Equal("bbb222", tags[12]);
    }

    [Fact]
    public void MapMirrors_ExactLaterOrNotYet()
    {
        var revisions = new[] { 3L, 5, 7, 12, 13 }.Select(r => new SvnRevision { Revision = r }).ToList();
        var tags = new SortedDictionary<long, string> { [5] = "h5", [12] = "h12" };

        SvnLog.MapMirrors(revisions, tags);

        Assert.Equal(new[] { "in svn-r5", "svn-r5", "in svn-r12", "svn-r12", "not mirrored yet" }, revisions.Select(r => r.MirrorText));
        Assert.Equal(new[] { "h5", "h5", "h12", "h12", null }, revisions.Select(r => r.MirrorHash));
        Assert.True(revisions[1].MirrorExact);
        Assert.False(revisions[4].IsMirrored);
    }

    private sealed class SyncSteps(List<SvnStepProgress> sink) : IProgress<SvnStepProgress>
    {
        public void Report(SvnStepProgress value) { lock (sink) sink.Add(value); }
    }

    private sealed class NoLog : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { }
    }

    [Fact]
    public async Task Ship_PlansEveryStep_AndMarksTheFailingOne()
    {
        var steps = new List<SvnStepProgress>();
        var missing = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "no-such-wc-" + Guid.NewGuid().ToString("N")[..8]);
        var flow = new SvnWorkflow(missing, "work", new NoLog()) { Steps = new SyncSteps(steps) };

        await Assert.ThrowsAsync<UserFacingException>(() => flow.ShipAsync("msg"));

        var plan = steps.Where(s => s.State == SvnStepState.Pending).Select(s => s.Id).ToList();
        Assert.Equal(new[]
        {
            SvnWorkflow.StepCheck, SvnWorkflow.StepPrepare, SvnWorkflow.StepApply, SvnWorkflow.StepCommit,
            SvnWorkflow.StepUpdate, SvnWorkflow.StepRecord, SvnWorkflow.StepMerge,
        }, plan);
        var last = steps.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal(SvnStepState.Failed, last[SvnWorkflow.StepCheck].State);
        Assert.False(string.IsNullOrEmpty(last[SvnWorkflow.StepCheck].Message));
        Assert.All(plan.Skip(1), id => Assert.Equal(SvnStepState.Skipped, last[id].State));
    }

    [Fact]
    public async Task Sync_PlansOnlyTheSyncSteps()
    {
        var steps = new List<SvnStepProgress>();
        var flow = new SvnWorkflow(Path.Combine(Path.GetTempPath(), "repo-sync-tests", "nope-" + Guid.NewGuid().ToString("N")[..8]), "work", new NoLog())
        {
            Steps = new SyncSteps(steps),
        };

        await Assert.ThrowsAsync<UserFacingException>(() => flow.SyncFromSvnAsync(dryRun: false));

        Assert.Equal(new[] { SvnWorkflow.StepUpdate, SvnWorkflow.StepRecord, SvnWorkflow.StepMerge },
            steps.Where(s => s.State == SvnStepState.Pending).Select(s => s.Id));
        Assert.Equal(SvnStepState.Failed, steps.Last(s => s.Id == SvnWorkflow.StepUpdate).State);
    }
}
