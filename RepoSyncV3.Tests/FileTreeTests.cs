using System.Collections.ObjectModel;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using Xunit;

namespace RepoSyncV3.Tests;

/// <summary>Changes / History file tree: folder folding, order, depth and collapsed folders.</summary>
public sealed class FileTreeTests
{
    private static FileChange F(string path) => new() { Path = path, Code = 'M' };

    private static string Show(IEnumerable<object> rows) => string.Join(" | ", rows.Select(r => r switch
    {
        FileTreeFolder d => $"{d.Depth}:{d.Name}/({d.FileCount})",
        FileChange f => $"{f.TreeDepth}:{f.FileName}",
        _ => "?",
    }));

    [Fact]
    public void FoldersFirst_SingleChildChainsFolded()
    {
        var rows = FileTreeView<FileChange>.Build(new[] { F("README.md"), F("src/app/views/Main.cs"), F("src/app/views/Home.cs"), F("src/app/App.cs"), F("docs/a.md") },
            new HashSet<string>());
        Assert.Equal("0:docs/(1) | 1:a.md | 0:src/app/(3) | 1:views/(2) | 2:Home.cs | 2:Main.cs | 1:App.cs | 0:README.md", Show(rows));
    }

    [Fact]
    public void CollapsedFolderHidesItsRows()
    {
        var rows = FileTreeView<FileChange>.Build(new[] { F("src/a/x.cs"), F("src/a/y.cs"), F("src/b.cs") }, new HashSet<string> { "src/a" });
        Assert.Equal("0:src/(3) | 1:a/(2) | 1:b.cs", Show(rows));
        Assert.False(rows.OfType<FileTreeFolder>().Single(f => f.Path == "src/a").IsExpanded);
    }

    [Fact]
    public void ListModeResetsDepth_TreeModeFollowsSource()
    {
        var src = new ObservableCollection<FileChange> { F("a/b.cs") };
        var view = new FileTreeView<FileChange>(src, isTree: true);
        Assert.Equal(2, view.Items.Cast<object>().Count());
        Assert.Equal(1, src[0].TreeDepth);
        view.IsTree = false;
        Assert.Same(src, view.Items);
        Assert.Equal(0, src[0].TreeDepth);
        view.IsTree = true;
        src.Add(F("c.cs"));
        Assert.Equal("0:a/(1) | 1:b.cs | 0:c.cs", Show(view.Items.Cast<object>()));
    }

    private sealed class Tick(string path, bool canCheck = true) : RepoSyncV3.ViewModels.ObservableObject, ICheckableTreeFile
    {
        private bool _on;
        public string Path { get; } = path;
        public string FileName => System.IO.Path.GetFileName(Path);
        public int TreeDepth { get; set; }
        public bool CanCheck { get; } = canCheck;
        public bool IsChecked { get => _on; set => Set(ref _on, value); }
    }

    [Fact]
    public void FolderCheckbox_TicksSendableFiles_AndShowsPartial()
    {
        var a = new Tick("src/a.cs");
        var b = new Tick("src/b.cs");
        var locked = new Tick("src/c.cs", canCheck: false);
        var view = new FileTreeView<Tick>(new ObservableCollection<Tick> { a, b, locked }, isTree: true);
        var folder = view.Items.OfType<FileTreeFolder>().Single();
        Assert.False(folder.IsChecked);

        var raised = 0;
        folder.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FileTreeFolder.IsChecked)) raised++; };
        a.IsChecked = true;
        Assert.Null(folder.IsChecked);
        Assert.True(raised > 0);

        folder.IsChecked = true;
        Assert.True(b.IsChecked);
        Assert.False(locked.IsChecked);
        Assert.True(folder.IsChecked);
    }
}
