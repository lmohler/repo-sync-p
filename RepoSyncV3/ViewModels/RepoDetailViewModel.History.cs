using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class RepoDetailViewModel
{
    // =================================================================================== history

    public ObservableCollection<CommitInfo> Commits { get; } = new();
    public ObservableCollection<CommitFile> CommitFiles { get; } = new();
    public IReadOnlyList<string> HistoryScopes { get; } = new[] { "current", "default", "all" };

    private int _historyMax = 300;
    private string _historyScope = "current";
    private CommitInfo? _selectedCommit;
    private CommitFile? _selectedCommitFile;
    private string _commitBody = string.Empty;
    private List<DiffLine> _commitDiff = new();
    private string _historyFilter = string.Empty;
    private List<CommitInfo> _allCommits = new();

    public string HistoryScope
    {
        get => _historyScope;
        set
        {
            if (!Set(ref _historyScope, value)) return;
            // a new scope replaces a "showing only X" filter
            SetBranchFilter(null);
            _ = LoadHistoryAsync();
        }
    }

    // ---- branch filter: clicking a branch label shows only that branch's commits (log <branch>)

    private string? _branchFilter;
    private ICommand? _filterBranchCommand;
    private ICommand? _clearBranchFilterCommand;

    /// <summary>The branch the list is narrowed to, or null for the scope picked in the combo box.</summary>
    public string? BranchFilter => _branchFilter;
    public bool IsBranchFiltered => _branchFilter is not null;

    /// <summary>Parameter: the clicked <see cref="RefLabel"/> (or a branch name).</summary>
    public ICommand FilterBranchCommand => _filterBranchCommand ??= Cmd(async p =>
    {
        var name = p switch { RefLabel { IsBranch: true } l => l.Name, string n => n, _ => null };
        if (string.IsNullOrEmpty(name) || name == _branchFilter) return;
        SetBranchFilter(name);
        await LoadHistoryAsync();
    });

    public ICommand ClearBranchFilterCommand => _clearBranchFilterCommand ??= Cmd(async _ =>
    {
        SetBranchFilter(null);
        await LoadHistoryAsync();
    });

    private void SetBranchFilter(string? name)
    {
        if (_branchFilter == name) return;
        _branchFilter = name;
        OnPropertyChanged(nameof(BranchFilter));
        OnPropertyChanged(nameof(IsBranchFiltered));
    }

    /// <summary>What the log is given: the clicked branch, else the scope.</summary>
    private string HistoryReference => _branchFilter ?? _historyScope switch
    {
        "default" => Repo.DefaultRef,
        "all" => "--all",
        _ => "HEAD",
    };

    /// <summary>Splits each commit's refs into labels colored like its graph line (or like the same commit in the
    /// full list, for search results that have no graph).</summary>
    private void LabelCommits(IEnumerable<CommitInfo> commits)
    {
        var remotes = new List<string> { "origin", "upstream" };
        if (Repo.RemoteName is { } rn) remotes.Add(rn);
        var current = Repo.Detached ? null : Repo.Branch;
        var colors = new Dictionary<string, int>();
        foreach (var c in _allCommits)
            if (c.Graph is not null) colors[c.Hash] = c.Graph.DotColor;
        foreach (var c in commits)
        {
            var color = c.Graph?.DotColor ?? (colors.TryGetValue(c.Hash, out var k) ? k : 0);
            c.Labels = RefLabels.Parse(c.Refs, current, remotes, color);
        }
    }

    public string HistoryFilter
    {
        get => _historyFilter;
        set
        {
            if (!Set(ref _historyFilter, value)) return;
            if (_historySearchMode == "filter") ApplyHistoryFilter();
            else if (value.Length == 0) { _searchResults = null; ApplyHistoryFilter(); }
        }
    }

    public CommitInfo? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (Set(ref _selectedCommit, value)) _ = LoadCommitAsync();
        }
    }

    public CommitFile? SelectedCommitFile
    {
        get => _selectedCommitFile;
        set
        {
            if (Set(ref _selectedCommitFile, value)) _ = LoadCommitDiffAsync();
        }
    }

    public string CommitBody { get => _commitBody; private set => Set(ref _commitBody, value); }
    public List<DiffLine> CommitDiff
    {
        get => _commitDiff;
        private set
        {
            if (Set(ref _commitDiff, value))
                OnPropertyChanged(nameof(CommitSplitDiff));
        }
    }

    public List<SideBySideDiffRow> CommitSplitDiff => SideBySideDiffRow.FromUnified(CommitDiff);

    private async Task LoadHistoryAsync()
    {
        var reference = HistoryReference;
        var commits = await Git.GetLogAsync(Repo.Path, reference, _historyMax);
        if (Repo.HasUpstream)
        {
            var unpushed = await Git.GetUnpushedHashesAsync(Repo.Path, Repo.Upstream!);
            foreach (var c in commits) c.IsUnpushed = unpushed.Contains(c.Hash);
        }
        else if (Repo.RemoteName is not null)
        {
            // an unpublished branch: everything not on the server's default branch is local-only
            var unpushed = await Git.GetUnpushedHashesAsync(Repo.Path, Repo.DefaultRef);
            foreach (var c in commits) c.IsUnpushed = unpushed.Contains(c.Hash);
        }
        CommitGraph.Layout(commits);
        _allCommits = commits;
        LabelCommits(commits);
        _searchResults = null;
        var keep = SelectedCommit?.Hash;
        ApplyHistoryFilter();
        SelectedCommit = Commits.FirstOrDefault(c => c.Hash == keep) ?? Commits.FirstOrDefault();
    }

    private void ApplyHistoryFilter()
    {
        if (_searchResults is not null)
        {
            Replace(Commits, _searchResults);
            OnPropertyChanged(nameof(ShowGraph));
            return;
        }
        Replace(Commits, _allCommits.Where(c => _historyFilter.Length == 0
            || c.Subject.Contains(_historyFilter, StringComparison.OrdinalIgnoreCase)
            || c.Author.Contains(_historyFilter, StringComparison.OrdinalIgnoreCase)
            || c.Hash.StartsWith(_historyFilter, StringComparison.OrdinalIgnoreCase)
            || c.Refs.Contains(_historyFilter, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(ShowGraph));
    }

    /// <summary>The graph only makes sense for the full, unfiltered list.</summary>
    public bool ShowGraph => _historyFilter.Length == 0 && _searchResults is null;

    private List<CommitInfo>? _searchResults;
    private string _historySearchMode = "filter";

    /// <summary>filter (loaded list), message, code (-S, added/removed text), file (path) - the last three search all of history.</summary>
    public string HistorySearchMode
    {
        get => _historySearchMode;
        set
        {
            if (!Set(ref _historySearchMode, value)) return;
            _searchResults = null;
            ApplyHistoryFilter();
        }
    }

    public async Task SearchHistoryAsync()
    {
        var text = _historyFilter.Trim();
        if (_historySearchMode == "filter" || text.Length == 0)
        {
            _searchResults = null;
            ApplyHistoryFilter();
            return;
        }
        var reference = HistoryReference;
        List<CommitInfo> found = _historySearchMode switch
        {
            "code" => await Git.GetLogAsync(Repo.Path, reference, 500, search: new[] { "-S", text }),
            "file" => await Git.GetLogAsync(Repo.Path, reference, 500, paths: new[] { $":(glob,icase)**/*{text}*" }),
            _ => await Git.GetLogAsync(Repo.Path, reference, 500, search: new[] { "-i", $"--grep={text}" }),
        };
        LabelCommits(found);
        _searchResults = found;
        ApplyHistoryFilter();
        SelectedCommit = Commits.FirstOrDefault();
    }

    private async Task LoadCommitAsync()
    {
        CommitFiles.Clear();
        CommitDiff = new List<DiffLine>();
        if (SelectedCommit is not { } c)
        {
            CommitBody = string.Empty;
            return;
        }
        CommitBody = await Git.GetCommitBodyAsync(Repo.Path, c.Hash);
        Replace(CommitFiles, await Git.GetCommitFilesAsync(Repo.Path, c.Hash));
        if (CommitFiles.Count == 1) SelectedCommitFile = CommitFiles[0];
        else CommitDiff = await Git.GetCommitDiffAsync(Repo.Path, c.Hash, null, IgnoreWhitespace);
    }

    private async Task LoadCommitDiffAsync()
    {
        if (SelectedCommit is not { } c) return;
        CommitDiff = await Git.GetCommitDiffAsync(Repo.Path, c.Hash, SelectedCommitFile?.Path, IgnoreWhitespace);
    }
}
