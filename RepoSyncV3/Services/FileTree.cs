using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RepoSyncV3.Models;

namespace RepoSyncV3.Services;

/// <summary>A folder row in a file tree. Single-child folder chains are folded into one row ("src/app/views"),
/// like VS Code's compact folders.</summary>
public sealed class FileTreeFolder : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public required string Name { get; init; }
    /// <summary>Repo-relative, '/'-separated, no trailing slash.</summary>
    public required string Path { get; init; }
    public int Depth { get; init; }
    /// <summary>Every file under this folder (all levels), as the source list's items.</summary>
    public IReadOnlyList<object> Files { get; init; } = Array.Empty<object>();
    public int FileCount => Files.Count;
    /// <summary>Changes tab: which side the folder is on (picks Stage vs Unstage); null in History.</summary>
    public bool? Staged => (Files.FirstOrDefault() as FileChange)?.Staged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            Toggled?.Invoke(this);
        }
    }

    internal Action<FileTreeFolder>? Toggled;

    // ---- SVN send list: one checkbox ticks every sendable file under the folder

    private IEnumerable<ICheckableTreeFile> Checkable => Files.OfType<ICheckableTreeFile>().Where(f => f.CanCheck);
    public bool IsCheckable => Checkable.Any();

    /// <summary>True when every sendable file under it is ticked, false when none, null (partial) otherwise.</summary>
    public bool? IsChecked
    {
        get
        {
            var all = Checkable.Select(f => f.IsChecked).Distinct().ToList();
            return all.Count == 1 ? all[0] : all.Count == 0 ? false : null;
        }
        set
        {
            var on = value ?? true;
            foreach (var f in Checkable.ToList()) f.IsChecked = on;
        }
    }

    /// <summary>Follow the files' ticks; <see cref="Detach"/> when the row is thrown away.</summary>
    internal void Attach()
    {
        foreach (var f in Files.OfType<ICheckableTreeFile>()) f.PropertyChanged += File_PropertyChanged;
    }

    internal void Detach()
    {
        foreach (var f in Files.OfType<ICheckableTreeFile>()) f.PropertyChanged -= File_PropertyChanged;
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(IsChecked));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Shows a flat file list either as-is or as a folder tree. In tree mode <see cref="Items"/> holds
/// <see cref="FileTreeFolder"/> rows and the source's own items (so selection, commands and templates keep working);
/// each file's indent comes from its TreeDepth. Collapsed folders stay collapsed across refreshes.</summary>
public sealed class FileTreeView<T> : INotifyPropertyChanged where T : class, ITreeFile
{
    private readonly ObservableCollection<T> _source;
    private readonly ObservableCollection<object> _flat = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool _isTree;
    private bool _pending;

    public FileTreeView(ObservableCollection<T> source, bool isTree)
    {
        _source = source;
        _isTree = isTree;
        _source.CollectionChanged += Source_CollectionChanged;
        if (isTree) Rebuild();
    }

    /// <summary>What the list binds to: the source in list mode, folders + files in tree mode.</summary>
    public IEnumerable Items => _isTree ? _flat : _source;

    public bool IsTree
    {
        get => _isTree;
        set
        {
            if (_isTree == value) return;
            _isTree = value;
            if (value) Rebuild();
            else
            {
                foreach (var f in _source) f.TreeDepth = 0;
                ClearRows();
            }
            OnPropertyChanged(nameof(IsTree));
            OnPropertyChanged(nameof(Items));
        }
    }

    public void ExpandAll(bool expand)
    {
        _collapsed.Clear();
        if (!expand)
            foreach (var f in _flat.OfType<FileTreeFolder>()) _collapsed.Add(f.Path);
        Rebuild();
    }

    // Replace() clears and re-adds one by one; build once after the batch instead of once per item.
    private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isTree || _pending) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        // not on the dispatcher thread (tests, or an App owned by another thread): no batch to wait for
        if (dispatcher is null || !dispatcher.CheckAccess()) { Rebuild(); return; }
        _pending = true;
        dispatcher.BeginInvoke(() => { _pending = false; if (_isTree) Rebuild(); }, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private void Rebuild()
    {
        ClearRows();
        foreach (var row in Build(_source, _collapsed, f => { if (f.IsExpanded) _collapsed.Remove(f.Path); else _collapsed.Add(f.Path); Rebuild(); }))
        {
            (row as FileTreeFolder)?.Attach();
            _flat.Add(row);
        }
    }

    private void ClearRows()
    {
        foreach (var f in _flat.OfType<FileTreeFolder>()) f.Detach();
        _flat.Clear();
    }

    /// <summary>Folders first (alphabetical), then files, at every level. Hidden rows (under a collapsed folder) are left out.</summary>
    public static List<object> Build(IEnumerable<T> files, ISet<string> collapsed, Action<FileTreeFolder>? toggled = null)
    {
        var root = new Node(string.Empty, string.Empty);
        foreach (var f in files)
        {
            var parts = f.Path.Trim('/').Split('/'); // SVN log paths start with '/'
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var path = node.Path.Length == 0 ? parts[i] : $"{node.Path}/{parts[i]}";
                if (!node.Children.TryGetValue(parts[i], out var child)) node.Children[parts[i]] = child = new Node(parts[i], path);
                node = child;
            }
            node.Files.Add(f);
        }

        var rows = new List<object>();
        Emit(root, 0, rows, collapsed, toggled);
        return rows;
    }

    private static void Emit(Node node, int depth, List<object> rows, ISet<string> collapsed, Action<FileTreeFolder>? toggled)
    {
        foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            // fold a chain of folders that only hold one folder each: a/b/c
            var c = child;
            var name = c.Name;
            while (c.Files.Count == 0 && c.Children.Count == 1)
            {
                c = c.Children.Values.First();
                name = $"{name}/{c.Name}";
            }
            var folder = new FileTreeFolder { Name = name, Path = c.Path, Depth = depth, Files = c.AllFiles().ToList() };
            folder.IsExpanded = !collapsed.Contains(c.Path);
            folder.Toggled = toggled;
            rows.Add(folder);
            if (folder.IsExpanded) Emit(c, depth + 1, rows, collapsed, toggled);
        }
        foreach (var f in node.Files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
        {
            f.TreeDepth = depth;
            rows.Add(f);
        }
    }

    private sealed class Node(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public SortedDictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<T> Files { get; } = new();
        public IEnumerable<object> AllFiles() => Files.Concat(Children.Values.SelectMany(c => c.AllFiles()));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
