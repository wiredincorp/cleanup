using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Cleanup.Interop;
using Cleanup.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.ViewModels;

/// <summary>State for the Applications page. Kept alive for the whole session so
/// switching pages does not rescan; refresh is explicit or follows a removal.</summary>
public sealed partial class ApplicationsViewModel : ObservableObject
{
    private readonly List<AppItemViewModel> _all = [];
    private readonly List<AppItemViewModel> _selection = [];
    private CancellationTokenSource? _removalCancellation;
    private IReadOnlyDictionary<string, string> _driveNames = new Dictionary<string, string>();
    private int _driveScan;
    private int _activityScan;
    private bool _backgroundDeclined;
    private bool _recentBySize;
    private string? _backgroundLog;
    private RecentActivity.Snapshot _activity = RecentActivity.Snapshot.Empty;

    public static ApplicationsViewModel Shared { get; } = new();

    public ApplicationsViewModel()
    {
        SettingsService.Changed += OnSettingsChanged;
    }

    public static IReadOnlyList<string> TypeFilters { get; } = ["All apps", "Desktop apps", "Windows Installer", "Store apps", "Games"];

    public static IReadOnlyList<string> SortOptions { get; } = ["Name", "Publisher", "Size", "Install date", "Type", "Drive", "Recently running"];

    [ObservableProperty]
    public partial ObservableCollection<AppItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial AppItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial int SelectionCount { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AppItemViewModel> SelectionIcons { get; set; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int TypeFilterIndex { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    public string NameSortHeader => SortHeader(0, "Name");
    public string TypeSortHeader => SortHeader(4, "Type");
    public string SizeSortHeader => SortIndex == 6 && _recentBySize
        ? $"Size {(SortDescending ? "↓" : "↑")}"
        : SortHeader(2, "Size");
    public string InstalledSortHeader => SortIndex == 6 && !_recentBySize
        ? $"Last run {(SortDescending ? "↓" : "↑")}"
        : SortIndex == 6 ? "Last run" : SortHeader(3, "Installed");
    public string DriveSortHeader => SortHeader(5, "Drive");

    private string SortHeader(int column, string label) => SortIndex == column ? $"{label} {(SortDescending ? "↓" : "↑")}" : label;

    public void SortByColumn(int column)
    {
        // Size reorders the recently-run list without leaving that view.
        if (SortIndex == 6 && column == 2)
        {
            if (!_recentBySize)
            {
                _recentBySize = true;
                if (!SortDescending)
                {
                    SortDescending = true;
                }
                else
                {
                    ApplyView();
                }
            }
            else
            {
                SortDescending = !SortDescending;
            }
            NotifySortHeaders();
            return;
        }
        // Last run flips newest and oldest without leaving Recently running.
        if (SortIndex == 6 && column == 3)
        {
            if (_recentBySize)
            {
                _recentBySize = false;
                if (!SortDescending)
                {
                    SortDescending = true;
                }
                else
                {
                    ApplyView();
                }
            }
            else
            {
                SortDescending = !SortDescending;
            }
            NotifySortHeaders();
            return;
        }
        if (_recentBySize)
        {
            _recentBySize = false;
            NotifySortHeaders();
        }
        if (column == SortIndex) SortDescending = !SortDescending;
        else SortIndex = column;
    }

    private void NotifySortHeaders()
    {
        foreach (string property in new[] { nameof(NameSortHeader), nameof(TypeSortHeader), nameof(SizeSortHeader), nameof(InstalledSortHeader), nameof(DriveSortHeader) })
            OnPropertyChanged(property);
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRemoving { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsMessageOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity MessageSeverity { get; set; }

    [ObservableProperty]
    public partial string MessageTitle { get; set; } = "";

    [ObservableProperty]
    public partial string MessageText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanReviewLeftovers { get; set; }

    [ObservableProperty]
    public partial string RemovalTitle { get; set; } = "";

    [ObservableProperty]
    public partial string RemovalApp { get; set; } = "";

    [ObservableProperty]
    public partial double RemovalProgress { get; set; }

    [ObservableProperty]
    public partial bool IsStopRequested { get; set; }

    /// <summary>Apps removed by the last removal, captured before the list refreshes.</summary>
    public List<AppRecord> RemovedApps { get; } = [];

    public IReadOnlyList<AppItemViewModel> SelectedItems => _selection;

    public bool IsEmpty => HasLoaded && !IsLoading && Items.Count == 0;

    public bool ShowDetails => SelectionCount == 1 && Selected is not null;

    public bool ShowMultiple => SelectionCount > 1;
    public bool HasSelection => SelectionCount > 0;
    public bool HasAdditionalSelection => SelectionCount > 6;
    public string AdditionalSelectionText => $"+{Math.Max(0, SelectionCount - 6)}";
    public string AdditionalSelectionDescription => $"{Math.Max(0, SelectionCount - 6)} more selected applications";

    public bool CanRemoveSelected => Selected is { CanRemove: true } && !IsRemoving;

    public int RemovableSelectionCount => _selection.Count(i => i.CanRemove);

    public bool CanRemoveMultiple => RemovableSelectionCount > 0 && !IsRemoving;
    public bool CanRefresh => !IsLoading && !IsRemoving;

    public string MultipleTitle => $"{SelectionCount} apps selected";

    public string RemoveMultipleText => $"Remove {RemovableSelectionCount} apps";

    public string MultipleSummary
    {
        get
        {
            ulong bytes = (ulong)_selection.Sum(i => (decimal)(i.ShownBytes ?? 0));
            string size = AppItemViewModel.FormatSize(bytes) is { } s ? $"{s}. " : "";
            int blocked = _selection.Count - RemovableSelectionCount;
            string blockedText = blocked > 0 ? $"{blocked} can't be removed here and will be skipped. " : "";
            return $"{size}{blockedText}Each app's own uninstaller runs one at a time.";
        }
    }

    public IEnumerable<string> SelectedNames => _selection.Select(i => i.Name);

    partial void OnItemsChanged(ObservableCollection<AppItemViewModel> value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CanRefresh)); }

    partial void OnIsRemovingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanRemoveMultiple));
        OnPropertyChanged(nameof(CanRefresh));
    }

    partial void OnSelectedChanged(AppItemViewModel? value)
    {
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(CanRemoveSelected));
    }

    partial void OnSearchTextChanged(string value) => ApplyView();

    partial void OnTypeFilterIndexChanged(int value) => ApplyView();

    partial void OnSortIndexChanged(int value)
    {
        if (value != 6 && _recentBySize)
        {
            _recentBySize = false;
        }
        bool descending = value is 2 or 3 or 6;
        if (value == 6)
        {
            _ = LoadActivityAsync(++_activityScan);
        }
        if (SortDescending != descending) SortDescending = descending;
        else ApplyView();
        NotifySortHeaders();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        NotifySortHeaders();
        ApplyView();
    }

    public void UpdateSelection(IEnumerable<AppItemViewModel> items)
    {
        _selection.Clear();
        _selection.AddRange(items);
        SelectionCount = _selection.Count;
        Selected = _selection.Count == 1 ? _selection[0] : null;
        SelectionIcons = _selection.Take(6).ToArray();
        foreach (string name in new[]
        {
            nameof(ShowDetails), nameof(ShowMultiple), nameof(HasSelection), nameof(HasAdditionalSelection),
            nameof(AdditionalSelectionText), nameof(AdditionalSelectionDescription), nameof(CanRemoveMultiple), nameof(MultipleTitle),
            nameof(RemoveMultipleText), nameof(MultipleSummary), nameof(SelectedNames), nameof(RemovableSelectionCount),
        })
        {
            OnPropertyChanged(name);
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (!HasLoaded && !IsLoading)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading || IsRemoving)
        {
            return;
        }
        IsLoading = true;
        var timing = Stopwatch.StartNew();
        string? selectedId = Selected?.Record.Id;
        try
        {
            InventoryScanResult scan = await NativeCore.ScanInventoryAsync(null, CancellationToken.None);
            _all.Clear();
            _all.AddRange(scan.Records.Select(r => new AppItemViewModel(r)));
            HasLoaded = true;
            ApplyView();
            _ = LoadDriveNamesAsync(++_driveScan);
            if (SortIndex == 6)
            {
                _ = LoadActivityAsync(++_activityScan);
            }
            StartGameScan();
            StartSizeScan();
            Selected = selectedId is null ? null : Items.FirstOrDefault(i => i.Record.Id == selectedId);
            PerformanceAudit.InventoryReady(_all.Count, timing.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            ShowMessage(InfoBarSeverity.Error, "Couldn't list installed apps", e.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Runs each app's official uninstaller one at a time, reports verified
    /// outcomes, records them in History, and offers leftover review.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyList<AppItemViewModel> items, nint ownerWindow)
    {
        List<AppItemViewModel> queue = items.Where(i => i.CanRemove).ToList();
        if (IsRemoving || queue.Count == 0)
        {
            return;
        }
        IsStopRequested = false;
        RemovalProgress = 0;
        IsRemoving = true;
        _removalCancellation = new CancellationTokenSource();
        CanReviewLeftovers = false;
        RemovedApps.Clear();
        var problems = new List<string>();
        bool restart = false;
        string lastStatus = "", lastMessage = "";
        try
        {
            for (int index = 0; index < queue.Count; index++)
            {
                if (IsStopRequested)
                {
                    problems.Add($"Stopped before {queue.Count - index} {(queue.Count - index == 1 ? "app" : "apps")}; nothing else was changed.");
                    break;
                }
                AppItemViewModel item = queue[index];
                IsMessageOpen = false;
                RemovalTitle = queue.Count > 1 ? $"Removing {index + 1} of {queue.Count}" : "Removing";
                RemovalApp = item.Name;
                RemovalProgress = index * 100.0 / queue.Count;
                string status, message;
                try
                {
                    string json = await NativeCore.UninstallAsync(item.Record.Id, allowElevation: true, ownerWindow, null, _removalCancellation.Token);
                    using JsonDocument report = JsonDocument.Parse(json);
                    status = report.RootElement.GetProperty("status").GetString() ?? "failed";
                    message = report.RootElement.GetProperty("message").GetString() ?? "";
                }
                catch (Exception e)
                {
                    (status, message) = ("failed", e.Message);
                }
                (lastStatus, lastMessage) = (status, message);
                try
                {
                    await Features.RecordUninstallAsync(item.Name, item.Record.Publisher, status, message);
                }
                catch (Exception)
                {
                    // History is informational; a failure to record never blocks removal.
                }

                if (status is "removed" or "removedRestartRequired")
                {
                    RemovedApps.Add(item.Record);
                    restart |= status == "removedRestartRequired";
                }
                else if (status != "notInstalled")
                {
                    problems.Add($"{item.Name}: {Describe(status, message)}");
                }
                // An untracked helper may still be changing the system.
                if (status is "detached" or "uncertain") break;
            }
        }
        finally
        {
            IsRemoving = false;
            _removalCancellation.Dispose();
            _removalCancellation = null;
        }

        CanReviewLeftovers = RemovedApps.Any(r => r.Technology != "msix");
        string leftoverHint = CanReviewLeftovers ? " Review leftovers to clean up files and settings it left behind." : "";
        if (queue.Count == 1)
        {
            AppItemViewModel item = queue[0];
            (InfoBarSeverity severity, string title) = lastStatus switch
            {
                "removed" => (InfoBarSeverity.Success, $"{item.Name} was removed"),
                "removedRestartRequired" => (InfoBarSeverity.Warning, $"{item.Name} was removed. Restart Windows to finish."),
                "notInstalled" => (InfoBarSeverity.Informational, $"{item.Name} is no longer installed"),
                "cancelled" => (InfoBarSeverity.Informational, "Removal cancelled"),
                "stillInstalled" => (InfoBarSeverity.Warning, $"{item.Name} is still installed"),
                "uncertain" or "detached" => (InfoBarSeverity.Warning, $"Couldn't confirm {item.Name} was removed"),
                "rejected" => (InfoBarSeverity.Error, $"{item.Name} can't be removed here"),
                _ => (InfoBarSeverity.Error, $"Couldn't remove {item.Name}"),
            };
            string text = lastStatus.StartsWith("removed", StringComparison.Ordinal) ? leftoverHint.Trim() : lastMessage;
            ShowMessage(severity, title, text);
        }
        else
        {
            int removed = RemovedApps.Count;
            InfoBarSeverity severity = problems.Count == 0 ? InfoBarSeverity.Success
                : removed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            string text = string.Join("\n", problems)
                + (restart ? "\nRestart Windows to finish removing some apps." : "")
                + leftoverHint;
            ShowMessage(severity, $"Removed {removed} of {queue.Count} apps", text.Trim());
        }
        await RefreshAsync();
    }

    /// <summary>
    /// Lets the current uninstaller finish normally and skips the apps still
    /// waiting. Interrupting an uninstaller midway is never attempted.
    /// </summary>
    public void StopQueue() => IsStopRequested = true;

    /// <summary>Context that keeps leftover matching away from apps that are still installed.</summary>
    public LeftoverAppInfo LeftoverContext(AppRecord removed) => new(
        removed.DisplayName,
        removed.Publisher,
        removed.InstallLocation,
        removed.Technology,
        _all.Where(i => i.Record.Id != removed.Id).Select(i => i.Record.DisplayName).ToArray(),
        _all.Where(i => i.Record.Id != removed.Id && !string.IsNullOrWhiteSpace(i.Record.InstallLocation))
            .Select(i => i.Record.InstallLocation!)
            .ToArray());

    public void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        IsMessageOpen = true;
    }

    public static void OpenLocation(AppItemViewModel item)
    {
        if (item.HasLocation)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            // Explorer does not use the CRT escaping rules for a quoted trailing backslash.
            // Trim it while preserving drive/share roots so paths containing spaces open correctly.
            start.ArgumentList.Add(Path.TrimEndingDirectorySeparator(item.Record.InstallLocation!));
            Process.Start(start);
        }
    }

    /// <summary>Opens the default browser on a web search for the app's name and publisher. Returns why it could not, or null.</summary>
    public static string? SearchOnline(AppItemViewModel item)
    {
        string query = string.Join(" ", new[] { item.Name, item.Publisher }.Where(text => !string.IsNullOrWhiteSpace(text)));
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}") { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Win32Exception error)
        {
            return error.Message;
        }
    }

    public static void OpenWindowsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });

    private static string Describe(string status, string message) => status switch
    {
        "cancelled" => "cancelled",
        "stillInstalled" => "the uninstaller closed but the app is still installed",
        "uncertain" or "detached" => "removal couldn't be confirmed",
        _ => string.IsNullOrWhiteSpace(message) ? "couldn't be removed" : message,
    };

    // Games are found after the apps are listed. A newer scan replaces an older one, so a slow scan never wins.
    private int _gameScan;
    private string[] _gameFolders = [];

    private void StartGameScan()
    {
        _gameFolders = SettingsService.Current.GameFolders.ToArray();
        IReadOnlyList<AppRecord> installed = _all.Where(item => !item.IsGame).Select(item => item.Record).ToArray();
        _ = LoadGamesAsync(++_gameScan, installed, _gameFolders);
    }

    private async Task LoadGamesAsync(int scan, IReadOnlyList<AppRecord> installed, IReadOnlyList<string> folders)
    {
        try
        {
            IReadOnlyList<GameEntry> games = await GameLibrary.ScanAsync(installed, folders);
            if (scan != _gameScan) return;
            _all.RemoveAll(item => item.IsGame);
            _all.AddRange(games.Select(game => new AppItemViewModel(game.Record, game.Launch)));
            foreach (AppItemViewModel item in _all) item.UpdateDriveName(_driveNames);
            ApplyView();
            StartSizeScan();
        }
        catch (Exception error)
        {
            ShowMessage(InfoBarSeverity.Warning, "Couldn't find games", error.Message);
        }
    }

    private CancellationTokenSource? _sizeScan;
    private DispatcherQueue? _sizesUi;
    private int _sizeResort;

    /// <summary>Fills in sizes the installer did not record, one folder at a time, without blocking the list.</summary>
    private void StartSizeScan()
    {
        _sizesUi ??= DispatcherQueue.GetForCurrentThread();
        _sizeScan?.Cancel();
        _sizeScan = new CancellationTokenSource();
        CancellationToken token = _sizeScan.Token;
        DispatcherQueue? queue = _sizesUi;
        AppItemViewModel[] pending = _all.Where(item => item.ShownBytes is null).ToArray();
        if (pending.Length == 0 || queue is null)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            foreach (AppItemViewModel item in pending)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                string? icon = IconLoader.ResolvePath(item.Record);
                string? executable = icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? icon : null;
                ulong? size;
                try
                {
                    size = InstallSize.Measure(item.Record.InstallLocation, executable, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (size is not > 0 || token.IsCancellationRequested)
                {
                    continue;
                }
                queue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    item.SetMeasuredSize(size.Value);
                    RefreshSizeSummary();
                    if (SortIndex == 2 || (SortIndex == 6 && _recentBySize))
                    {
                        _ = ResortSizesAsync();
                    }
                });
            }
        });
    }

    private async Task ResortSizesAsync()
    {
        int resort = ++_sizeResort;
        await Task.Delay(400);
        if (resort == _sizeResort && (SortIndex == 2 || (SortIndex == 6 && _recentBySize)))
        {
            ApplyView();
        }
    }

    private void OnSettingsChanged()
    {
        if (HasLoaded && !IsLoading && !SettingsService.Current.GameFolders.SequenceEqual(_gameFolders, StringComparer.OrdinalIgnoreCase))
        {
            StartGameScan();
        }
    }

    private async Task LoadDriveNamesAsync(int scan)
    {
        // Drive probes are bounded by StorageService and run after the list is already visible.
        // A disconnected disk must not delay the application inventory.
        try
        {
            DriveItemViewModel[] drives = await StorageService.ReadAsync();
            if (scan != _driveScan) return;
            _driveNames = drives.ToDictionary(drive => drive.Letter, drive =>
                string.IsNullOrWhiteSpace(drive.Label) ? drive.Letter : $"{drive.Label} ({drive.Letter})", StringComparer.OrdinalIgnoreCase);
            foreach (AppItemViewModel item in _all) item.UpdateDriveName(_driveNames);
            if (SortIndex == 5) ApplyView();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Paths still identify drive letters if volume names cannot be read.
        }
    }

    private async Task LoadActivityAsync(int scan)
    {
        try
        {
            RecentActivity.Snapshot snapshot = await Task.Run(RecentActivity.Read);
            if (scan != _activityScan || SortIndex != 6)
            {
                return;
            }
            bool liveLog = snapshot.IncludesBackgroundLog;
            string savedLog = Path.Combine(Path.GetTempPath(), "cleanup-run-history.txt");
            bool savedFresh = File.Exists(savedLog) && File.GetLastWriteTimeUtc(savedLog) > DateTime.UtcNow.AddHours(-12);
            if (!liveLog && File.Exists(savedLog))
            {
                _backgroundLog = savedLog;
                snapshot = RecentActivity.WithBackgroundLog(snapshot, savedLog);
            }
            if (!liveLog && !savedFresh && !_backgroundDeclined)
            {
                string log = savedLog;
                RecentActivity.BackgroundLogResult result = await Task.Run(() => RecentActivity.RequestBackgroundLog(log));
                if (result == RecentActivity.BackgroundLogResult.Written)
                {
                    _backgroundLog = log;
                    snapshot = RecentActivity.WithBackgroundLog(snapshot, log);
                }
                else if (result == RecentActivity.BackgroundLogResult.Declined)
                {
                    _backgroundDeclined = true;
                    ShowMessage(InfoBarSeverity.Informational, "More run history needs approval", "Windows keeps most last-run times for an administrator. Approve the prompt to fill in programs you have opened.");
                }
            }
            if (scan != _activityScan || SortIndex != 6)
            {
                return;
            }
            _activity = snapshot;
            ApplyView();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The full list stays; rows simply keep their publisher line.
        }
    }

    private void StampActivity()
    {
        bool show = SortIndex == 6;
        foreach (AppItemViewModel item in _all)
        {
            string? icon = IconLoader.ResolvePath(item.Record);
            string? executable = icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? icon : null;
            if (item.IsGame && executable is not null && item.Record.InstallLocation is { Length: > 3 } folder
                && !executable.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                executable = null;
            }
            RecentActivity.RunHit hit = show ? _activity.Match(item.Record.InstallLocation, executable, item.Record.PackageFamilyName, item.Name) : default;
            DateTime? ran = hit.LastRan;
            if (item.Record.LastRun is DateTime played && (ran is null || played > ran))
            {
                ran = played;
            }
            item.ApplyRunState(show, hit.Running, ran);
        }
    }

    private void ApplyView()
    {
        StampActivity();
        string query = SearchText.Trim();
        IEnumerable<AppItemViewModel> view = _all;
        if (query.Length > 0)
        {
            view = view.Where(i => i.Matches(query));
        }
        view = TypeFilterIndex switch
        {
            1 => view.Where(i => i.Record.Technology == "win32"),
            2 => view.Where(i => i.Record.Technology == "msi"),
            3 => view.Where(i => i.Record.Technology == "msix"),
            4 => view.Where(i => i.IsGame),
            _ => view,
        };
        IOrderedEnumerable<AppItemViewModel> Order<T>(Func<AppItemViewModel, T> key, IComparer<T>? comparer = null) =>
            SortDescending ? view.OrderByDescending(key, comparer) : view.OrderBy(key, comparer);
        IOrderedEnumerable<AppItemViewModel> OrderKnown<T>(Func<AppItemViewModel, bool> missing, Func<AppItemViewModel, T> key, IComparer<T>? comparer = null)
        {
            var knownFirst = view.OrderBy(missing);
            return SortDescending ? knownFirst.ThenByDescending(key, comparer) : knownFirst.ThenBy(key, comparer);
        }
        var sorted = SortIndex switch
        {
            1 => OrderKnown(i => string.IsNullOrWhiteSpace(i.Publisher), i => i.Publisher, StringComparer.CurrentCultureIgnoreCase),
            2 => OrderKnown(i => i.ShownBytes is null, i => i.ShownBytes),
            3 => OrderKnown(i => i.InstallDate is null, i => i.InstallDate),
            4 => Order(i => i.TypeText, StringComparer.CurrentCultureIgnoreCase),
            5 => OrderKnown(i => i.DriveRoot is null, i => i.DriveText, StringComparer.CurrentCultureIgnoreCase),
            // Running programs first, then ones that ran in the last 30 days. Size can reorder this same list.
            6 => _recentBySize
                ? SortDescending
                    ? view.OrderBy(i => i.ShownBytes is null).ThenByDescending(i => i.ShownBytes ?? 0)
                    : view.OrderBy(i => i.ShownBytes is null).ThenBy(i => i.ShownBytes ?? 0)
                : SortDescending
                    ? view.OrderBy(i => !i.IsRunningNow && i.LastRan is null).ThenByDescending(i => i.IsRunningNow ? DateTime.MaxValue : i.LastRan ?? DateTime.MinValue)
                    : view.OrderBy(i => !i.IsRunningNow && i.LastRan is null).ThenBy(i => i.IsRunningNow ? DateTime.MaxValue : i.LastRan ?? DateTime.MinValue),
            _ => Order(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        view = sorted.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);

        AppItemViewModel? selected = Selected;
        Items = new ObservableCollection<AppItemViewModel>(view);
        if (selected is not null && !Items.Contains(selected))
        {
            Selected = null;
        }

        RefreshSizeSummary();
    }

    private void RefreshSizeSummary()
    {
        ulong totalBytes = (ulong)_all.Sum(i => (decimal)(i.ShownBytes ?? 0));
        string size = AppItemViewModel.FormatSize(totalBytes) is { } s ? $" · {s}" : "";
        Summary = Items.Count == _all.Count
            ? $"{_all.Count} apps{size}"
            : $"Showing {Items.Count} of {_all.Count} apps";
        OnPropertyChanged(nameof(MultipleSummary));
    }
}
