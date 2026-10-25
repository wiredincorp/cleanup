namespace Cleanup.Services;

/// <summary>
/// Walks one folder tree and lists its children by size, name, or path.
/// shortcut: every file stays in memory so search can sort it; switch to an on-disk index if a scan uses too much RAM.
/// </summary>
internal static class FolderScan
{
    internal enum Column { Size, Name, Path }

    internal sealed class Node
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public bool IsFolder { get; init; }
        public ulong Size { get; set; }
        public List<Node> Children { get; } = new();
    }

    internal sealed class Result
    {
        public required Node Root { get; init; }
        public List<string> Log { get; } = new();
        public int FolderCount { get; set; }
        public int FileCount { get; set; }
        public int Skipped { get; set; }
    }

    public static Result Read(string root, CancellationToken cancel, Action<string>? progress = null, Action<Node>? onFolder = null)
    {
        string fullRoot = Path.GetFullPath(root);
        var result = new Result
        {
            Root = new Node
            {
                Name = DisplayName(fullRoot),
                FullPath = fullRoot,
                IsFolder = true,
            },
        };
        result.Log.Add($"{Stamp()} Scanning {fullRoot}");
        if (!Directory.Exists(fullRoot))
        {
            result.Skipped++;
            result.Log.Add($"{Stamp()} Skipped {fullRoot}: folder is missing");
            Finish(result, fullRoot);
            return result;
        }

        var pending = new Stack<Walk>();
        pending.Push(new Walk(result.Root));
        try
        {
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                Walk walk = pending.Peek();
                if (!walk.Started)
                {
                    walk.Started = true;
                    try
                    {
                        walk.Entries = Directory.EnumerateFileSystemEntries(walk.Node.FullPath).GetEnumerator();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        NoteSkip(result, walk.Node.FullPath, error.Message);
                        FinishWalk(pending, onFolder);
                        continue;
                    }
                }

                string? entry = null;
                bool moved = false;
                try
                {
                    moved = walk.Entries!.MoveNext();
                    if (moved)
                    {
                        entry = walk.Entries.Current;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    NoteSkip(result, walk.Node.FullPath, error.Message);
                    FinishWalk(pending, onFolder);
                    continue;
                }
                if (!moved)
                {
                    FinishWalk(pending, onFolder);
                    continue;
                }

                try
                {
                    FileAttributes attributes = File.GetAttributes(entry!);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        NoteSkip(result, entry!, "link or junction");
                        continue;
                    }
                    if (IsProtectedSystem(entry!) || IsDriveRootSystemFile(entry!, attributes))
                    {
                        NoteSkip(result, entry!, "needed for Windows");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var folder = new Node { Name = Path.GetFileName(entry!), FullPath = entry!, IsFolder = true };
                        walk.Node.Children.Add(folder);
                        result.FolderCount++;
                        pending.Push(new Walk(folder));
                        progress?.Invoke(entry!);
                    }
                    else
                    {
                        ulong length = Length(entry!);
                        walk.Node.Children.Add(new Node { Name = Path.GetFileName(entry!), FullPath = entry!, Size = length });
                        walk.Node.Size += length;
                        result.FileCount++;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    NoteSkip(result, entry!, error.Message);
                }
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                pending.Pop().Dispose();
            }
        }

        Finish(result, fullRoot);
        return result;
    }

    /// <summary>
    /// Measures each child of <paramref name="root"/> and reports it when that child's size is final.
    /// Likely-large folders are measured first. The caller sorts, so a bigger folder can still move above one already shown.
    /// </summary>
    public static Result ReadStreaming(string root, CancellationToken cancel, Action<Node> onChild, Action<string>? progress = null)
    {
        string fullRoot = Path.GetFullPath(root);
        var result = new Result
        {
            Root = new Node { Name = DisplayName(fullRoot), FullPath = fullRoot, IsFolder = true },
        };
        result.Log.Add($"{Stamp()} Scanning {fullRoot}");
        if (!Directory.Exists(fullRoot))
        {
            result.Skipped++;
            result.Log.Add($"{Stamp()} Skipped {fullRoot}: folder is missing");
            Finish(result, fullRoot);
            return result;
        }

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(fullRoot)
                .OrderBy(ScanRank)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            NoteSkip(result, fullRoot, error.Message);
            Finish(result, fullRoot);
            return result;
        }

        foreach (string entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        NoteSkip(result, entry, "link or junction");
                        continue;
                    }
                    if (IsProtectedSystem(entry) || IsDriveRootSystemFile(entry, attributes))
                    {
                        NoteSkip(result, entry, "needed for Windows");
                        continue;
                    }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Result child = Read(entry, cancel, progress);
                    result.Root.Children.Add(child.Root);
                    result.Root.Size += child.Root.Size;
                    result.FolderCount += 1 + child.FolderCount;
                    result.FileCount += child.FileCount;
                    result.Skipped += child.Skipped;
                    result.Log.AddRange(child.Log);
                    onChild(child.Root);
                }
                else
                {
                    ulong length = Length(entry);
                    var file = new Node { Name = Path.GetFileName(entry), FullPath = entry, Size = length };
                    result.Root.Children.Add(file);
                    result.Root.Size += length;
                    result.FileCount++;
                    onChild(file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                NoteSkip(result, entry, error.Message);
            }
        }

        Finish(result, fullRoot);
        return result;
    }

    public static IReadOnlyList<Node> Rows(Node scope, string? query, Column column, bool descending)
    {
        IEnumerable<Node> source = string.IsNullOrWhiteSpace(query) ? scope.Children : Matching(scope, query.Trim());
        IOrderedEnumerable<Node> ordered = column switch
        {
            Column.Name => descending
                ? source.OrderByDescending(node => node.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
            Column.Path => descending
                ? source.OrderByDescending(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? source.OrderByDescending(node => node.Size)
                : source.OrderBy(node => node.Size),
        };
        return ordered.ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<Node> SortList(IReadOnlyList<Node> entries, string? query, Column column, bool descending)
    {
        IEnumerable<Node> source = string.IsNullOrWhiteSpace(query)
            ? entries
            : entries.Where(node => node.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        IOrderedEnumerable<Node> ordered = column switch
        {
            Column.Name => descending
                ? source.OrderByDescending(node => node.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
            Column.Path => descending
                ? source.OrderByDescending(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? source.OrderByDescending(node => node.Size)
                : source.OrderBy(node => node.Size),
        };
        return ordered.ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<Node> Matching(Node scope, string query)
    {
        var found = new List<Node>();
        var pending = new Stack<Node>();
        foreach (Node child in scope.Children)
        {
            pending.Push(child);
        }
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(node);
            }
            for (int index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
        return found;
    }

    private sealed class Walk
    {
        public Walk(Node node) => Node = node;
        public Node Node { get; }
        public IEnumerator<string>? Entries { get; set; }
        public bool Started { get; set; }
        public void Dispose()
        {
            Entries?.Dispose();
            Entries = null;
        }
    }

    private static void FinishWalk(Stack<Walk> pending, Action<Node>? onFolder)
    {
        Walk done = pending.Pop();
        done.Dispose();
        if (pending.Count > 0)
        {
            pending.Peek().Node.Size += done.Node.Size;
        }
        onFolder?.Invoke(done.Node);
    }

    internal static bool IsProtectedSystem(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full) ?? "";
        string relative = root.Length > 0 && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].TrimStart('\\')
            : full;
        if (relative.Length == 0)
        {
            return false;
        }
        string[] parts = relative.Split('\\');
        if (IsSystemFolder(parts[0]))
        {
            return true;
        }
        if (parts.Length >= 2 && parts[0].StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) && IsWindowsComponent(parts[1]))
        {
            return true;
        }
        string file = parts[^1];
        return file.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
            || file.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || file.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRootSystemFile(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.System) == 0)
        {
            return false;
        }
        string? parent = Path.GetDirectoryName(path);
        string? root = Path.GetPathRoot(path);
        return parent is not null && root is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(root),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemFolder(string name) =>
        name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
        || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
        || name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Config.Msi", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Boot", StringComparison.OrdinalIgnoreCase)
        || name.Equals("EFI", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsComponent(string name) =>
        name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Windows NT", StringComparison.OrdinalIgnoreCase);

    private static ulong Length(string path)
    {
        try
        {
            return (ulong)new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void NoteSkip(Result result, string path, string reason)
    {
        result.Skipped++;
        // shortcut: the log keeps the first 200 skips; raise the cap when a fix needs every blocked path.
        if (result.Skipped <= 200)
        {
            result.Log.Add($"{Stamp()} Skipped {path}: {reason}");
        }
        else if (result.Skipped == 201)
        {
            result.Log.Add($"{Stamp()} Further skipped paths are counted but not listed");
        }
    }

    private static void Finish(Result result, string root) =>
        result.Log.Add($"{Stamp()} Finished {root}: {result.FolderCount} folders, {result.FileCount} files, {result.Skipped} skipped, {result.Root.Size} bytes");

    private static int ScanRank(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string[] first = ["Users", "Program Files", "Program Files (x86)", "Windows", "Games", "Steam", "XboxGames", "ProgramData"];
        int index = Array.FindIndex(first, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? first.Length : index;
    }

    private static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length == 0 ? path : name;
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");
}
