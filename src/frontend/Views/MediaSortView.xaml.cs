using System.Diagnostics;
using Cleanup.Interop;
using Cleanup.Services;
using Cleanup.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Cleanup.Views;

/// <summary>
/// Photos and Videos: choose a folder → review the moves → done. Only that kind
/// of file moves, into date folders built from when it was taken or recorded,
/// or when it last changed if the file doesn't say. Planning, moving, and undo
/// are the organizer's, so the same previews, History entries, and checks apply.
/// </summary>
public sealed partial class MediaSortView : UserControl
{
    private static readonly string[] DateKinds = ["yearMonth", "year", "month"];
    private static readonly string[] DateKindNames = ["Year / Month", "Year", "Month"];

    private string _media = "photos";
    private OrganizePlan? _plan;
    private KnownLocations? _locations;
    private string? _source;
    private string? _lastBatchId;
    private string? _lastFolder;
    private bool _busy;
    private bool _choosingFolder;
    private CancellationTokenSource? _cancellation;

    // Options (date folders and name handling are remembered; name handling is shared with Organize).
    private string _dateFolders = DateKinds.Contains(SettingsService.Current.MediaDateFolders) ? SettingsService.Current.MediaDateFolders : "yearMonth";
    private string _destination = "";

    public MediaSortView()
    {
        InitializeComponent();
        foreach (UIElement step in new UIElement[] { StartPanel, PreviewPanel, DonePanel }) Motion.ShowHide(step, rise: 14);
        ApplyMedia();
        Loaded += OnLoaded;
        Unloaded += (_, _) => OperationUpdates.UndoCompleted -= OnBatchUndone;
    }

    /// <summary>"photos" or "videos".</summary>
    public string Media
    {
        get => _media;
        set { _media = value == "videos" ? "videos" : "photos"; ApplyMedia(); }
    }

    private bool IsVideos => _media == "videos";

    private string Files(int count) => $"{count} {(IsVideos ? "video" : "photo")}{(count == 1 ? "" : "s")}";

    private void ApplyMedia()
    {
        TitleText.Text = IsVideos ? "Videos" : "Photos";
        StartIcon.Glyph = PreviewIcon.Glyph = IsVideos ? "" : "";
        LibraryLink.Content = IsVideos ? "Videos" : "Pictures";
        string folders = _dateFolders switch { "year" => "year folders", "month" => "month folders", _ => "year and month folders" };
        StartDescription.Text = IsVideos
            ? $"Videos are sorted into {folders} by the date they were recorded, or the date they last changed when a video doesn't say. Other files stay where they are. You'll see every move before anything happens."
            : $"Photos are sorted into {folders} by the date they were taken, or the date they last changed when a photo doesn't say. Other files stay where they are. You'll see every move before anything happens.";
        UpdateSummary();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The event is static, so a view that stayed subscribed after it left would never be released.
        OperationUpdates.UndoCompleted -= OnBatchUndone;
        OperationUpdates.UndoCompleted += OnBatchUndone;
        try { _locations ??= await Features.GetLocationsAsync(); }
        catch (Exception error) { Show(InfoBarSeverity.Error, "Couldn't load folders", error.Message); }
    }

    // ---- Steps ---------------------------------------------------------------

    private void ShowStep(UIElement step)
    {
        foreach (UIElement panel in new UIElement[] { StartPanel, PreviewPanel, DonePanel })
            panel.Visibility = panel == step ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStartOverClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _plan = null; _source = null; _lastBatchId = null; _lastFolder = null; Status.IsOpen = false;
        ShowStep(StartPanel);
    }

    // ---- Choosing a folder ---------------------------------------------------

    private async void OnLibraryClick(object sender, RoutedEventArgs e) { if ((IsVideos ? _locations?.Videos : _locations?.Pictures) is { } path) await OpenFolderAsync(path); }

    private async void OnDownloadsClick(object sender, RoutedEventArgs e) { if (_locations?.Downloads is { } path) await OpenFolderAsync(path); }

    private async void OnDesktopClick(object sender, RoutedEventArgs e) { if (_locations?.Desktop is { } path) await OpenFolderAsync(path); }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _choosingFolder) return;
        _choosingFolder = true;
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
            if (await picker.PickSingleFolderAsync() is { } folder) await OpenFolderAsync(folder.Path);
        }
        catch (Exception error) { Show(InfoBarSeverity.Error, "Couldn't open the folder picker", error.Message); }
        finally { _choosingFolder = false; }
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = IsVideos ? "Sort the videos in this folder" : "Sort the photos in this folder";
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFolder>().FirstOrDefault() is { } folder) await OpenFolderAsync(folder.Path);
            else Show(InfoBarSeverity.Informational, "Drop a folder, not a file", $"cleanup sorts the {(IsVideos ? "videos" : "photos")} inside a folder.");
        }
        catch (Exception error) { Show(InfoBarSeverity.Error, "Couldn't read the dropped folder", error.Message); }
    }

    private async Task OpenFolderAsync(string path)
    {
        if (_busy) return;
        _source = path;
        await PreviewAsync();
    }

    // ---- Preview -------------------------------------------------------------

    private async Task PreviewAsync()
    {
        if (_busy || _source is null) return;
        SetBusy(true);
        Status.IsOpen = false;
        try
        {
            _plan = await Features.PlanAsync(_source, "date", _dateFolders,
                string.IsNullOrWhiteSpace(_destination) ? null : _destination,
                skipCollisions: SettingsService.Current.OrganizeCollisions == "skip", media: _media, cancellation: _cancellation!.Token);
            FolderName.Text = Path.GetFileName(_plan.Source.TrimEnd('\\')) is { Length: > 0 } name ? name : _plan.Source;
            FolderPath.Text = _plan.Source;
            PreviewList.ItemsSource = _plan.Operations;
            PreviewList.SelectAll();
            // Newest first: years, or months when sorting by month alone.
            GroupTiles.ItemsSource = _plan.Operations
                .GroupBy(o => o.DestinationFolder.Split('\\')[0], StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GroupSummary(g.Key, $"{Files(g.Count())} · {AppItemViewModel.FormatSize((ulong)g.Sum(o => (decimal)o.SizeBytes)) ?? "0 B"}", ""))
                .ToArray();
            bool none = _plan.Operations.Length == 0;
            TidyState.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            PreviewList.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            string kind = IsVideos ? "videos" : "photos";
            TidyTitle.Text = $"No {kind} to sort here";
            TidyDetail.Text = _plan.Skipped.Length > 0
                ? $"Some {kind} are staying where they are; see why below."
                : $"There are no {kind} directly in this folder. Subfolders are left as they are.";
            SkippedButton.Content = $"{_plan.Skipped.Length} staying in place";
            SkippedButton.Visibility = _plan.Skipped.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStep(PreviewPanel);
            UpdateSummary();
        }
        catch (Exception error) when (error is OperationCanceledException || error is NativeCoreException { Code: "cancelled" })
        {
            _plan = null;
            ShowStep(StartPanel);
            Show(InfoBarSeverity.Informational, "Preview cancelled", "No files were moved.");
        }
        catch (Exception error)
        {
            Show(InfoBarSeverity.Error, "Couldn't open this folder", error.Message);
            ShowStep(StartPanel);
        }
        finally { SetBusy(false); }
    }

    private void OnPreviewSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        PlannedOperation[] selected = PreviewList.SelectedItems.Cast<PlannedOperation>().ToArray();
        ApplyButton.IsEnabled = !_busy && selected.Length > 0;
        ApplyButton.Content = selected.Length > 0 ? $"Move {Files(selected.Length)}" : $"Move {(IsVideos ? "videos" : "photos")}";
        if (_plan is null) { SummaryText.Text = ""; return; }
        string others = _plan.OtherFiles > 0 ? $"{_plan.OtherFiles} other {(_plan.OtherFiles == 1 ? "file stays" : "files stay")} where {(_plan.OtherFiles == 1 ? "it is" : "they are")}" : "";
        if (_plan.Operations.Length == 0) { SummaryText.Text = others; return; }
        int folders = selected.Select(o => o.DestinationFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText.Text = $"{selected.Length} of {_plan.Operations.Length} selected · {AppItemViewModel.FormatSize((ulong)selected.Sum(i => (decimal)i.SizeBytes)) ?? "0 B"} into {folders} {(folders == 1 ? "folder" : "folders")}"
            + (others.Length > 0 ? $" · {others}" : "");
    }

    private async void OnSkippedClick(object sender, RoutedEventArgs e)
    {
        if (_plan is { } plan)
            await Dialog("Staying in place", string.Join("\n\n", plan.Skipped.Select(i => $"{i.FileName}\n{i.Reason}")), null, "Close").ShowAsync();
    }

    // ---- Moving --------------------------------------------------------------

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || _busy) return;
        PlannedOperation[] selected = PreviewList.SelectedItems.Cast<PlannedOperation>().ToArray();

        // Only ask about name clashes when there are some.
        PlannedOperation[] clashes = selected.Where(o => o.Renamed).ToArray();
        string handling = SettingsService.Current.OrganizeCollisions;
        if (clashes.Length > 0 && handling == "ask")
        {
            var ask = Dialog(
                $"{clashes.Length} {(clashes.Length == 1 ? "name is" : "names are")} already taken",
                $"{string.Join(", ", clashes.Take(5).Select(c => c.FileName))}{(clashes.Length > 5 ? ", …" : "")} already exist{(clashes.Length == 1 ? "s" : "")} in the destination. Nothing is ever overwritten.",
                "Keep both", "Cancel", secondary: "Skip these files");
            ContentDialogResult answer = await ask.ShowAsync();
            if (answer == ContentDialogResult.None) return;
            handling = answer == ContentDialogResult.Primary ? "keepBoth" : "skip";
        }
        if (handling == "skip") selected = selected.Where(o => !o.Renamed).ToArray();
        if (selected.Length == 0) return;

        var confirm = Dialog($"Move {Files(selected.Length)}?",
            "Only the selected files will move. Existing files will not be overwritten.\n\n" +
            string.Join("\n\n", selected.Select(o => $"{o.Source}\n→ {o.Destination}")), "Move files", "Cancel");
        confirm.DefaultButton = ContentDialogButton.Close;
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            string folderName = Path.GetFileName(plan.Source.TrimEnd('\\'));
            OrganizeApplyResult result = await Features.ApplyAsync(plan, selected, $"Sorted {(IsVideos ? "videos" : "photos")} in {folderName}", _cancellation!.Token);
            _lastBatchId = result.Moved > 0 ? result.BatchId : null;
            _lastFolder = plan.Destination;
            // Count date folders the way the preview summary does.
            int folders = selected.Select(o => o.DestinationFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            DoneIcon.Glyph = result.Failed > 0 ? "" : "";
            bool stopped = _cancellation.IsCancellationRequested;
            DoneTitle.Text = stopped ? $"Stopped after moving {Files(result.Moved)}" : result.Failed > 0
                ? $"Moved {result.Moved} of {Files(selected.Length)}"
                : $"Moved {Files(result.Moved)} into {folders} {(folders == 1 ? "folder" : "folders")}";
            DoneDetail.Text = result.Failed > 0
                ? string.Join("\n", result.Failures.Select(i => $"{i.FileName}: {i.Reason}"))
                : stopped ? "Completed moves are in History and can be undone where eligible. Remaining files were left in place." : "You can undo this now or later from History.";
            _plan = null;
            UndoButton.Visibility = _lastBatchId is null ? Visibility.Collapsed : Visibility.Visible;
            ShowStep(DonePanel);
        }
        catch (Exception error) { Show(InfoBarSeverity.Error, "Couldn't finish moving files", error.Message + " Check History before retrying; some moves may have completed."); }
        finally { SetBusy(false); }
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_lastBatchId is not { } batch || _busy) return;
        var confirm = Dialog("Undo these file moves?", "Only unchanged files with available original names can be restored. Conflicts will stay in place.", "Undo moves", "Cancel");
        confirm.DefaultButton = ContentDialogButton.Close;
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true, canCancel: false);
        try
        {
            UndoResult result = await Features.UndoAsync(batch);
            OperationUpdates.PublishUndo(batch, result);
        }
        catch (Exception error) { Show(InfoBarSeverity.Error, "Couldn't undo", error.Message); }
        finally { SetBusy(false); }
    }

    private void OnBatchUndone(string batch, UndoResult result)
    {
        if (_lastBatchId != batch) return;
        _lastBatchId = result.Conflicts.Length == 0 ? null : batch;
        _lastFolder = _source ?? _lastFolder;
        UndoButton.Visibility = _lastBatchId is null ? Visibility.Collapsed : Visibility.Visible;
        DoneIcon.Glyph = result.Conflicts.Length > 0 ? "" : "";
        DoneTitle.Text = $"Put back {Files(result.Restored)}";
        DoneDetail.Text = result.Conflicts.Length > 0
            ? string.Join("\n", result.Conflicts.Select(i => $"{i.FileName}: {i.Reason}"))
            : "All recorded moves in this batch have been restored.";
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_lastFolder is { } folder && Directory.Exists(folder))
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add(Path.TrimEndingDirectorySeparator(folder));
            Process.Start(start)?.Dispose();
        }
    }

    // ---- Options ---------------------------------------------------------------

    private async void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dates = new ComboBox { Header = "Folders", ItemsSource = DateKindNames, SelectedIndex = Math.Max(0, Array.IndexOf(DateKinds, _dateFolders)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var names = new ComboBox { Header = "When a name is already taken", ItemsSource = new[] { "Ask me", "Keep both (rename the moved file)", "Skip the file" }, SelectedIndex = SettingsService.Current.OrganizeCollisions switch { "keepBoth" => 1, "skip" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var destination = new TextBox { Header = "Put the folders in", PlaceholderText = "The same folder", Text = _destination };
        ContentDialog dialog = Dialog("Options", null, "Done", "Cancel");
        dialog.Content = new StackPanel { Spacing = 12, MinWidth = 340, Children = { dates, names, destination } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _dateFolders = DateKinds[Math.Max(0, dates.SelectedIndex)];
        _destination = destination.Text.Trim();
        string collisions = names.SelectedIndex switch { 1 => "keepBoth", 2 => "skip", _ => "ask" };
        SettingsService.Remember(s => { s.MediaDateFolders = _dateFolders; s.OrganizeCollisions = collisions; });
        ApplyMedia();
        if (_source is not null && PreviewPanel.Visibility == Visibility.Visible) await PreviewAsync();
    }

    // ---- Helpers ---------------------------------------------------------------

    private ContentDialog Dialog(string title, string? message, string? primary, string close, string? secondary = null) => new()
    {
        Title = title,
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        PrimaryButtonText = primary ?? "",
        SecondaryButtonText = secondary ?? "",
        CloseButtonText = close,
        DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
        Content = message is null ? null : new ScrollViewer { MaxHeight = 360, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Text = message } },
    };

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Status.Severity = severity; Status.Title = title; Status.Message = message; Status.IsOpen = true;
    }

    private void SetBusy(bool busy, bool canCancel = true)
    {
        _busy = busy;
        if (busy) { _cancellation = new CancellationTokenSource(); CancelButton.IsEnabled = true; }
        else { _cancellation?.Dispose(); _cancellation = null; }
        CancelButton.Visibility = busy && canCancel ? Visibility.Visible : Visibility.Collapsed;
        StartBusy.IsActive = busy && StartPanel.Visibility == Visibility.Visible;
        PreviewBusy.IsActive = busy && PreviewPanel.Visibility == Visibility.Visible;
        DropZone.Opacity = StartBusy.IsActive ? 0.4 : 1;
        DropZone.IsHitTestVisible = PreviewList.IsEnabled = UndoButton.IsEnabled = OpenFolderButton.IsEnabled = !busy;
        UpdateSummary();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
    }
}
