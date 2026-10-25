using System.Collections.ObjectModel;
using System.Diagnostics;
using Cleanup.Services;
using Cleanup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Cleanup.Views;

public sealed partial class FoldersPage : Page
{
    private static readonly object _gate = new();
    private readonly Dictionary<string, FolderScan.Node> _shown = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FolderScan.Node> _loaded = new();
    private static readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<FolderScan.Node> _ready = new();
    private readonly ObservableCollection<FolderRowViewModel> _rows = new();
    private static readonly List<string> _log = new();
    private readonly List<FolderScan.Node> _trail = new();
    private static CancellationTokenSource? _scan;
    private static FolderScan.Node? _root;
    private static FoldersPage? _page;
    private static bool _scanning;
    private static bool _background;
    private static bool _stoppedByToggle;
    private FolderScan.Column _column = FolderScan.Column.Size;
    private bool _descending = true;
    private bool _followSort;
    private static bool _started;
    private static int _generation;
    private static long _lastProgressTick;

    public FoldersPage()
    {
        InitializeComponent();
        FolderList.ItemsSource = _rows;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        Loaded += OnLoaded;
        _page = this;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _page = this;
        if (_scanning || _loaded.Count > 0)
        {
            ShowRows();
            Progress.IsIndeterminate = _scanning;
            ScanNote.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
            if (_scanning && _background)
            {
                ScanNote.Text = "Scanning in the background. This may take a minute. Other pages stay usable.";
            }
            ShowBackgroundButton();
            return;
        }
        if (_started)
        {
            return;
        }
        _started = true;
        await StartScanAsync(reset: true);
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await StartScanAsync(reset: true);

    private void OnBackgroundClick(object sender, RoutedEventArgs e)
    {
        _page = this;
        if (_background)
        {
            _background = false;
            ShowBackgroundButton();
            ScanNote.Text = "This may take a minute.";
            ScanNote.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _scanning ? "Background scan off. This scan is still running." : "Background scan off";
            return;
        }
        _background = true;
        ShowBackgroundButton();
        if (!_scanning)
        {
            _ = StartScanAsync(reset: _loaded.Count == 0);
        }
        ScanNote.Text = "Scanning in the background. This may take a minute. Other pages stay usable.";
        ScanNote.Visibility = Visibility.Visible;
        StatusText.Text = "Scanning in the background";
    }

    private void ShowBackgroundButton()
    {
        BackgroundScanButton.Content = _background ? "Background scan on" : "Scan in background";
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnBackInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = GoBack();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
        {
            return;
        }
        e.Handled = true;
        GoBack();
    }

    private bool GoBack()
    {
        lock (_gate)
        {
            if (_trail.Count == 0)
            {
                return false;
            }
            _trail.RemoveAt(_trail.Count - 1);
        }
        ShowRows();
        return true;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string? path = (FolderList.SelectedItem as FolderRowViewModel)?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            lock (_gate)
            {
                path = _trail.Count > 0 ? _trail[^1].FullPath : null;
            }
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        string arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        bool show = LogText.Visibility == Visibility.Collapsed;
        LogText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            RefreshLog();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ShowRows();

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || !Enum.TryParse(tag, out FolderScan.Column column))
        {
            return;
        }
        _followSort = true;
        if (_column == column)
        {
            _descending = !_descending;
        }
        else
        {
            _column = column;
            _descending = column == FolderScan.Column.Size;
        }
        ShowRows();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FolderRowViewModel row || !row.IsFolder || !_shown.TryGetValue(row.Path, out FolderScan.Node? node))
        {
            return;
        }
        lock (_gate)
        {
            _trail.Add(node);
        }
        ShowRows();
    }

    private async Task StartScanAsync(bool reset)
    {
        if (!reset && _scanning)
        {
            return;
        }
        _scan?.Cancel();
        var scan = new CancellationTokenSource();
        _scan = scan;
        int generation = ++_generation;
        if (reset)
        {
            lock (_gate)
            {
                _trail.Clear();
                _loaded.Clear();
                _seen.Clear();
                _root = new FolderScan.Node { Name = "This PC", FullPath = "", IsFolder = true };
                _log.Clear();
                _log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Folder scan started");
            }
            lock (_ready)
            {
                _ready.Clear();
            }
            _followSort = false;
            _rows.Clear();
            _shown.Clear();
        }
        else if (_root is null)
        {
            _root = new FolderScan.Node { Name = "This PC", FullPath = "", IsFolder = true };
        }
        Progress.IsIndeterminate = true;
        _scanning = true;
        ScanNote.Visibility = Visibility.Visible;
        if (!_background)
        {
            StatusText.Text = reset ? "Looking for drives" : "Scanning";
        }
        if (reset)
        {
            ShowRows();
            RefreshLog();
        }
        try
        {
            await Task.Run(() => ScanDrives(scan.Token, generation), scan.Token);
            Note(generation, "Scan finished");
            if (generation == _generation && _page is { } finished)
            {
                finished.StatusText.Text = "Scan finished";
                _background = false;
                finished.ShowBackgroundButton();
            }
        }
        catch (OperationCanceledException)
        {
            Note(generation, "Scan cancelled");
            if (generation == _generation && _page is { } cancelled)
            {
                cancelled.StatusText.Text = _stoppedByToggle ? "Background scan off" : "Scan cancelled";
                _background = false;
                cancelled.ShowBackgroundButton();
            }
        }
        catch (Exception error)
        {
            Note(generation, $"Scan stopped: {error.GetType().Name}: {error.Message}");
            if (generation == _generation && _page is { } stopped)
            {
                stopped.StatusText.Text = "Scan stopped. Open Scan log for the reason.";
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _scanning = false;
                _background = false;
                _stoppedByToggle = false;
                if (_page is { } page)
                {
                    page.Progress.IsIndeterminate = false;
                    page.ScanNote.Visibility = Visibility.Collapsed;
                    page.WriteLog();
                    page.ShowRows();
                    page.RefreshLog();
                }
            }
        }
    }

    private void ScanDrives(CancellationToken cancel, int generation)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancel.ThrowIfCancellationRequested();
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory)
            {
                Note(generation, $"{drive.Name} skipped ({drive.DriveType})");
                continue;
            }
            if (!drive.IsReady)
            {
                Note(generation, $"{drive.Name} skipped (not ready)");
                continue;
            }
            try
            {
                Note(generation, $"{drive.Name} Windows reports {drive.TotalSize - drive.TotalFreeSpace} bytes used of {drive.TotalSize}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Note(generation, $"{drive.Name} size could not be read: {error.Message}");
            }

            FolderScan.Result scan = FolderScan.Read(
                drive.RootDirectory.FullName,
                cancel,
                path => ReportProgress(generation, path),
                folder => QueueFolder(generation, folder));
            lock (_gate)
            {
                if (generation != _generation || _root is null)
                {
                    return;
                }
                _log.AddRange(scan.Log);
                _log.Add($"{DateTime.Now:HH:mm:ss} {drive.Name} counted {scan.Root.Size} bytes, {scan.FolderCount} folders, {scan.FileCount} files, {scan.Skipped} skipped");
            }
            Post(() =>
            {
                if (generation != _generation || _page is not { } page)
                {
                    return;
                }
                page.StatusText.Text = $"Finished {drive.Name}.";
                page.WriteLog();
                page.RefreshLog();
            });
        }
    }

    private void QueueFolder(int generation, FolderScan.Node folder)
    {
        lock (_gate)
        {
            if (generation != _generation || _root is null)
            {
                return;
            }
            if (!_seen.Add(folder.FullPath))
            {
                return;
            }
            _loaded.Add(folder);
            if (IsDriveRoot(folder.FullPath))
            {
                _root.Size += folder.Size;
            }
        }
        lock (_ready)
        {
            _ready.Enqueue(folder);
        }
        Post(() => _page?.ShowOne(generation));
    }

    private void ShowOne(int generation)
    {
        if (generation != _generation)
        {
            return;
        }
        FolderScan.Node? folder;
        lock (_ready)
        {
            if (_ready.Count == 0)
            {
                return;
            }
            folder = _ready.Dequeue();
        }
        bool atTop;
        lock (_gate)
        {
            atTop = _trail.Count == 0;
        }
        if (atTop && FolderMatches(folder) && !_shown.ContainsKey(folder.FullPath))
        {
            var row = RowFor(folder);
            _shown[folder.FullPath] = folder;
            if (_followSort)
            {
                _rows.Insert(SortedIndex(folder), row);
            }
            else
            {
                _rows.Add(row);
            }
            CountText.Text = $"{_rows.Count} folders";
            LocationText.Text = "This PC";
        }
        StatusText.Text = $"{folder.Name} · {Ui.Size(folder.Size)}";
    }

    private bool FolderMatches(FolderScan.Node folder) =>
        string.IsNullOrWhiteSpace(SearchBox.Text) || folder.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    private int SortedIndex(FolderScan.Node folder)
    {
        int lower = 0;
        int upper = _rows.Count;
        while (lower < upper)
        {
            int middle = (lower + upper) / 2;
            FolderScan.Node existing = _shown[_rows[middle].Path];
            if (CompareFolders(existing, folder) <= 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return lower;
    }

    private int CompareFolders(FolderScan.Node left, FolderScan.Node right)
    {
        int result = _column switch
        {
            FolderScan.Column.Name => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
            FolderScan.Column.Path => string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase),
            _ => left.Size.CompareTo(right.Size),
        };
        if (_descending)
        {
            result = -result;
        }
        return result == 0 ? string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) : result;
    }

    private static bool IsDriveRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(root),
            Path.TrimEndingDirectorySeparator(path),
            StringComparison.OrdinalIgnoreCase);
    }

    private static FolderRowViewModel RowFor(FolderScan.Node folder) => new()
    {
        Name = folder.Name,
        Path = folder.FullPath,
        SizeText = Ui.Size(folder.Size),
        IsFolder = folder.IsFolder,
    };

    private static void Post(Action action)
    {
        FoldersPage? page = _page;
        page?.DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_page, page))
            {
                action();
            }
        });
    }

    private void ReportProgress(int generation, string path)
    {
        long now = Environment.TickCount64;
        if (now - _lastProgressTick < 250)
        {
            return;
        }
        _lastProgressTick = now;
        Post(() =>
        {
            if (generation == _generation && _page is { } page)
            {
                page.StatusText.Text = path;
            }
        });
    }

    private void ShowRows()
    {
        FolderScan.Node? scope;
        IReadOnlyList<FolderScan.Node> rows;
        ulong shown;
        bool nested;
        lock (_gate)
        {
            nested = _trail.Count > 0;
            if (_root is null && !nested)
            {
                return;
            }
            if (nested)
            {
                scope = _trail[^1];
                rows = FolderScan.Rows(scope, SearchBox.Text, _column, _descending);
                shown = scope.Size;
            }
            else
            {
                scope = _root;
                rows = FolderScan.SortList(_loaded, SearchBox.Text, _column, _descending);
                shown = _root!.Size;
            }
        }
        _rows.Clear();
        _shown.Clear();
        foreach (FolderScan.Node node in rows)
        {
            _shown[node.FullPath] = node;
            _rows.Add(RowFor(node));
        }
        string place = string.IsNullOrEmpty(scope?.FullPath) ? "This PC" : scope.FullPath;
        LocationText.Text = place;
        CountText.Text = $"{rows.Count} folders · {Ui.Size(shown)}";
        BackButton.IsEnabled = nested;
        NameSort.Content = Header(FolderScan.Column.Name, "Name");
        PathSort.Content = Header(FolderScan.Column.Path, "Path");
        SizeSort.Content = Header(FolderScan.Column.Size, "Size");
    }

    private string Header(FolderScan.Column column, string label) =>
        _column == column ? $"{label} {(_descending ? "↓" : "↑")}" : label;

    private void Note(int generation, string line)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            }
        }
    }

    private void RefreshLog()
    {
        if (LogText.Visibility != Visibility.Visible)
        {
            return;
        }
        string[] lines;
        lock (_gate)
        {
            lines = _log.ToArray();
        }
        LogText.Text = string.Join(Environment.NewLine, lines);
    }

    private void WriteLog()
    {
        string[] lines;
        lock (_gate)
        {
            lines = _log.ToArray();
        }
        try
        {
            string directory = AppEnvironment.LocalStateDirectory;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "folder-scan.log");
            lock (_gate)
            {
                string listed = $"Log file: {path}";
                if (!_log.Contains(listed))
                {
                    _log.Add(listed);
                }
                lines = _log.ToArray();
            }
            File.WriteAllLines(path, lines);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _log.Add($"{DateTime.Now:HH:mm:ss} Could not write the scan log: {error.Message}");
            }
        }
    }
}
