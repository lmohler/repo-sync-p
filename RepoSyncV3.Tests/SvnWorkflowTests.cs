using System.Diagnostics;
using System.IO;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>The SVN round trip against a scratch SVN server (svnadmin create): working copy + git main mirror +
/// 'work' worktree, then Send / svn commit / Finish, Undo, and the mirror leaving local SVN work out.
/// Skipped (passes trivially) when svnadmin isn't on PATH.</summary>
public sealed class SvnWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-sync-tests", "svn-" + Guid.NewGuid().ToString("N")[..8]);
    private string Wc => Path.Combine(_root, "wc");
    private string Work => Path.Combine(_root, "wc-work");
    private readonly List<LogEntry> _log = new();

    private SvnWorkflow Flow() => new(Wc, "work", new SyncProgress(_log));

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests want the lines immediately.</summary>
    private sealed class SyncProgress(List<LogEntry> sink) : IProgress<LogEntry>
    {
        public void Report(LogEntry value) { lock (sink) sink.Add(value); }
    }

    private static bool HasSvnAdmin()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("svnadmin", "--version --quiet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> Run(string exe, string dir, params string[] args)
    {
        if (exe == "svnadmin")
        {
            using var p = Process.Start(new ProcessStartInfo("svnadmin") { ArgumentList = { args[0], args[1] }, UseShellExecute = false, CreateNoWindow = true })!;
            await p.WaitForExitAsync();
            return string.Empty;
        }
        var r = exe == "svn" ? await Svn.RunAsync(dir, args) : await Git.RunAsync(dir, args);
        if (!r.Success) throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: {r.CombinedOutput}");
        return r.StdOut;
    }

    /// <summary>Server with a.txt + b.txt, checked out to wc, git main mirroring r1, 'work' in wc-work.</summary>
    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_root);
        var server = Path.Combine(_root, "server");
        await Run("svnadmin", _root, "create", server);
        var url = "file:///" + server.Replace('\\', '/');
        await Run("svn", _root, "checkout", url, Wc);
        File.WriteAllText(Path.Combine(Wc, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(Wc, "b.txt"), "bee\n");
        await Run("svn", Wc, "add", "a.txt", "b.txt");
        await Run("svn", Wc, "commit", "-m", "first");
        await Run("svn", Wc, "update");

        await Run("git", Wc, "init", "-q", "-b", "main");
        await Run("git", Wc, "config", "core.autocrlf", "false");
        await Run("git", Wc, "config", "user.name", "Test User");
        await Run("git", Wc, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(Wc, ".gitignore"), ".svn/\n");
        var first = await Svn.MirrorCommitAsync(Wc, dryRun: false);
        Assert.True(first.Committed, first.Error);
        await Run("git", Wc, "worktree", "add", "-b", "work", Work, "main");
    }

    [Fact]
    public async Task Setup_FromUrl_BuildsMirrorAndWorktree_RerunSkips_MoveWorks()
    {
        if (!HasSvnAdmin()) return;
        Directory.CreateDirectory(_root);
        var server = Path.Combine(_root, "server");
        await Run("svnadmin", _root, "create", server);
        var url = "file:///" + server.Replace('\\', '/');

        // seed the server with a file and an svn:ignore rule, from a throwaway checkout
        var seed = Path.Combine(_root, "seed");
        await Run("svn", _root, "checkout", url, seed);
        File.WriteAllText(Path.Combine(seed, "a.txt"), "one\n");
        await Run("svn", seed, "add", "a.txt");
        await Run("svn", seed, "propset", "svn:ignore", "bin", ".");
        await Run("svn", seed, "commit", "-m", "first");

        var worktree = await Flow().SetupAsync(url, Work);

        Assert.Equal(Path.GetFullPath(Work), worktree);
        Assert.True(Svn.IsWorkingCopy(Wc));
        Assert.Contains("/bin", File.ReadAllText(Path.Combine(Wc, ".gitignore")));
        Assert.Equal("svn-r1", (await Run("git", Wc, "tag", "--list", "svn-r*")).Trim());
        Assert.Equal("work", (await Run("git", Work, "branch", "--show-current")).Trim());
        Assert.True(File.Exists(Path.Combine(Work, "a.txt")));
        Assert.Equal("false", (await Run("git", Wc, "config", "core.autocrlf")).Trim());

        // running it again on the finished layout changes nothing
        Assert.Equal(Path.GetFullPath(Work), await Flow().SetupAsync(null, Work));

        var moved = await Flow().MoveWorktreeAsync("wc-renamed");
        Assert.Equal(Path.Combine(_root, "wc-renamed"), moved);
        Assert.True(File.Exists(Path.Combine(moved, "a.txt")));
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public async Task Send_Commit_Finish_RoundTrip()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        Assert.Equal("svn-r1", (await Run("git", Wc, "tag", "--list", "svn-r*")).Trim());

        // work: change a, add c, delete b
        File.WriteAllText(Path.Combine(Work, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(Work, "c.txt"), "new\n");
        File.Delete(Path.Combine(Work, "b.txt"));
        await Run("git", Work, "add", "-A");
        await Run("git", Work, "commit", "-m", "my work");

        var compare = new SvnCompare(Wc, Work);
        await compare.GetStateAsync();
        var files = await compare.GetChangesAsync();
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, files.Select(f => f.Path));
        Assert.All(files, f => Assert.False(f.Uncommitted));

        Assert.Equal("Would send 3 file(s)", await Flow().SendAsync(Array.Empty<string>(), dryRun: true));
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(Wc, "a.txt")));

        await Flow().SendAsync(Array.Empty<string>(), dryRun: false, openCommitDialog: false);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(Wc, "a.txt")));
        var status = await Run("svn", Wc, "status");
        Assert.Contains("A       c.txt", status);
        Assert.Contains("D       b.txt", status);
        Assert.True(Flow().HasPendingSend);

        await Run("svn", Wc, "commit", "-m", "PROJ-1 my work");
        await Flow().SyncFromSvnAsync(dryRun: false);

        Assert.False(Flow().HasPendingSend);
        Assert.Contains("svn-r2", await Run("git", Wc, "tag", "--list", "svn-r*"));
        Assert.Equal("two\n", await Run("git", Wc, "show", "main:a.txt"));
        Assert.Equal(string.Empty, (await Run("git", Wc, "diff", "--name-only", "main", "work")).Trim());
        Assert.Empty(await new SvnCompare(Wc, Work).GetChangesAsync());
    }

    [Fact]
    public async Task Ship_WithMessage_CommitsSyncsAndReportsEveryStep()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        File.WriteAllText(Path.Combine(Work, "a.txt"), "shipped\n");
        await Run("git", Work, "commit", "-am", "ship it");

        var steps = new List<SvnStepProgress>();
        var flow = Flow();
        flow.Steps = new StepSink(steps);
        var outcome = await flow.ShipAsync("PROJ-2 shipped");

        Assert.StartsWith("Matches SVN r2", outcome);
        Assert.Contains("PROJ-2 shipped", await Run("svn", Wc, "log", "-r", "2"));
        Assert.Equal("shipped\n", await Run("git", Wc, "show", "svn-r2:a.txt"));
        var last = steps.GroupBy(s => s.Id).Select(g => g.Last()).ToList();
        Assert.Equal(7, last.Count);
        Assert.All(last, s => Assert.Equal(SvnStepState.Done, s.State));

        // the log lines r2 up with its mirror tag
        var revisions = await SvnLog.GetLogAsync(Wc, 10);
        SvnLog.MapMirrors(revisions, await SvnLog.GetMirrorTagsAsync(Wc));
        var r2 = Assert.Single(revisions, r => r.Revision == 2);
        Assert.Equal("svn-r2", r2.MirrorText);
        Assert.Equal((await Run("git", Wc, "rev-parse", "svn-r2^{commit}")).Trim(), r2.MirrorHash);
        Assert.Contains(r2.Paths, p => p.Path.EndsWith("/a.txt") || p.Path == "/a.txt");
    }

    [Fact]
    public async Task Ship_StopsOnUncommittedWork_AtTheCheckStep()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        File.WriteAllText(Path.Combine(Work, "a.txt"), "dirty\n");

        var steps = new List<SvnStepProgress>();
        var flow = Flow();
        flow.Steps = new StepSink(steps);
        await Assert.ThrowsAsync<UserFacingException>(() => flow.ShipAsync("msg"));

        Assert.Equal(SvnStepState.Failed, steps.Last(s => s.Id == SvnWorkflow.StepCheck).State);
        Assert.Equal(SvnStepState.Skipped, steps.Last(s => s.Id == SvnWorkflow.StepApply).State);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(Wc, "a.txt")));
    }

    private sealed class StepSink(List<SvnStepProgress> sink) : IProgress<SvnStepProgress>
    {
        public void Report(SvnStepProgress value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public async Task SendSomePaths_ThenUndo_RestoresWorkingCopy()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        File.WriteAllText(Path.Combine(Work, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(Work, "c.txt"), "new\n");
        await Run("git", Work, "add", "-A");
        await Run("git", Work, "commit", "-m", "my work");

        await Flow().SendAsync(new[] { "c.txt" }, dryRun: false, openCommitDialog: false);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(Wc, "a.txt")));
        Assert.True(File.Exists(Path.Combine(Wc, "c.txt")));

        await Flow().UndoAsync();
        Assert.False(File.Exists(Path.Combine(Wc, "c.txt")));
        Assert.Equal(string.Empty, (await Run("svn", Wc, "status", "-q")).Trim());
    }

    [Fact]
    public async Task Send_StopsOnUncommittedWork()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        File.WriteAllText(Path.Combine(Work, "a.txt"), "dirty\n");

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => Flow().SendAsync(Array.Empty<string>(), dryRun: false, openCommitDialog: false));
        Assert.Contains("uncommitted", ex.Message);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(Wc, "a.txt")));
        Assert.Contains(await new SvnCompare(Wc, Work).GetChangesAsync(), f => f.Path == "a.txt" && f.Uncommitted);
    }

    [Fact]
    public async Task Mirror_LeavesLocalSvnWorkOut()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        File.WriteAllText(Path.Combine(Wc, "a.txt"), "local edit\n");
        File.WriteAllText(Path.Combine(Wc, "loose.txt"), "unversioned\n");

        var result = await Svn.MirrorCommitAsync(Wc, dryRun: false);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Committed);
        Assert.Equal(2, result.LocalChangesKept);
        Assert.Equal("one\n", await Run("git", Wc, "show", "main:a.txt"));
    }

    [Fact]
    public async Task CommitOnWorkingCopy_IsRefused()
    {
        if (!HasSvnAdmin()) return;
        await SetupAsync();
        var actions = new RepoActions(Wc, new SyncProgress(_log));
        await Assert.ThrowsAsync<UserFacingException>(() => actions.CommitAsync("oops", amend: false));
        Assert.Equal(Wc, Svn.WorkingCopyFor(Work, Git.MainWorktreePath(Work)), ignoreCase: true);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
