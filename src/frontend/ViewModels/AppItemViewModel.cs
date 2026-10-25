using System.Globalization;
using Cleanup.Interop;
using Cleanup.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cleanup.ViewModels;

/// <summary>One row in the Applications list.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    private static readonly Color[] AvatarColors =
    [
        Color.FromArgb(255, 0x3A, 0x6E, 0xA5), Color.FromArgb(255, 0x5B, 0x5F, 0x97), Color.FromArgb(255, 0x2E, 0x7D, 0x6B),
        Color.FromArgb(255, 0x8A, 0x55, 0x8C), Color.FromArgb(255, 0x9A, 0x6B, 0x2F), Color.FromArgb(255, 0x4F, 0x6D, 0x7A),
        Color.FromArgb(255, 0x7A, 0x4E, 0x4E), Color.FromArgb(255, 0x3F, 0x7F, 0x3F),
    ];

    // Rows share one brush per color instead of creating a native brush for every application.
    private static readonly Brush[] AvatarBrushes = AvatarColors.Select(color => (Brush)new SolidColorBrush(color)).ToArray();

    // Icons are kept only for the most recently shown rows; see EnsureIcon.
    private const int MaxIconsInMemory = 160;
    private const int EvictionsPerCollection = 48;
    private static readonly LinkedList<AppItemViewModel> IconsInMemory = new();
    private static int _evictedSinceCollect;
    private LinkedListNode<AppItemViewModel>? _iconNode;
    private bool _iconLoading;
    private bool? _hasLocation;

    public AppItemViewModel(AppRecord record, LaunchTarget? launch = null)
    {
        Record = record;
        Launch = launch;
        Name = string.IsNullOrWhiteSpace(record.DisplayName) ? "(unnamed)" : record.DisplayName.Trim();
        Initial = char.ToUpperInvariant(Name.FirstOrDefault(char.IsLetterOrDigit, '?')).ToString();
        AvatarBrush = AvatarBrushes[(int)((uint)StableHash(Name) % AvatarColors.Length)];
        InstallDate = DateOnly.TryParseExact(record.InstallDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null;
        DriveRoot = InstallDriveRoot(record.InstallLocation);
        DriveText = DriveRoot?.TrimEnd('\\') ?? "Not reported";
        ListSubtitle = PublisherText;
        ActivityColumnText = DateText;
    }

    public AppRecord Record { get; }

    /// <summary>How a game is opened. Installed apps are opened from their own records instead, so this is null for them.</summary>
    public LaunchTarget? Launch { get; }

    /// <summary>Steam, Epic, and emulator entries are games; everything else is an installed app.</summary>
    public bool IsGame => Record.Technology is "steam" or "epic" or "emulator";

    public string Name { get; }

    public string Initial { get; }

    public Brush AvatarBrush { get; }

    public DateOnly? InstallDate { get; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));

    public string Publisher => Record.Publisher ?? "";

    public string PublisherText => string.IsNullOrWhiteSpace(Record.Publisher) ? "Unknown publisher" : Record.Publisher;

    /// <summary>Publisher under the app name.</summary>
    [ObservableProperty]
    public partial string ListSubtitle { get; set; } = "";

    /// <summary>Install date, or Running / the last run date while that sort is selected.</summary>
    [ObservableProperty]
    public partial string ActivityColumnText { get; set; } = "";

    internal bool IsRunningNow { get; private set; }

    internal DateTime? LastRan { get; private set; }

    internal int RunRank { get; private set; } = 2;

    internal void ApplyRunState(bool show, bool running, DateTime? lastRan)
    {
        IsRunningNow = show && running;
        LastRan = show ? lastRan : null;
        RunRank = !show ? 2 : running ? 0 : lastRan is DateTime when && when >= DateTime.Now - RecentActivity.RecentWindow ? 1 : 2;
        if (ListSubtitle != PublisherText)
        {
            ListSubtitle = PublisherText;
        }
        string column = DateText;
        if (show)
        {
            column = running ? "Running" : lastRan is DateTime ran ? ran.ToString("d", CultureInfo.CurrentCulture) : "";
        }
        if (ActivityColumnText != column)
        {
            ActivityColumnText = column;
        }
    }

    public string VersionText => Record.Version ?? "Not reported";

    private ulong? _measuredSize;

    /// <summary>Installer-reported size, or the size of the install folder once that has been added up.</summary>
    public ulong? ShownBytes => _measuredSize ?? Record.EstimatedSizeBytes;

    public string SizeText => FormatSize(ShownBytes) ?? "";

    public string SizeDetail => FormatSize(ShownBytes) ?? "Not reported";

    internal void SetMeasuredSize(ulong bytes)
    {
        if (bytes == 0 || Record.EstimatedSizeBytes is not null)
        {
            return;
        }
        _measuredSize = bytes;
        OnPropertyChanged(nameof(ShownBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeDetail));
    }

    public string DateText => InstallDate?.ToString("d", CultureInfo.CurrentCulture) ?? "";

    public string DateDetail => InstallDate?.ToString("D", CultureInfo.CurrentCulture) ?? "Not reported";

    public string TypeText => Record.Technology switch
    {
        "msix" => "Store app",
        "msi" => "Windows Installer",
        "steam" => "Steam game",
        "epic" => "Epic game",
        "emulator" => "Emulator game",
        _ => "Desktop app",
    };

    public string ScopeText => Record.Scope == "machine" ? "All users" : "Current user";

    public string LocationText => string.IsNullOrWhiteSpace(Record.InstallLocation) ? "Not reported" : Record.InstallLocation;

    public string? DriveRoot { get; }

    [ObservableProperty]
    public partial string DriveText { get; set; }

    internal void UpdateDriveName(IReadOnlyDictionary<string, string> names) =>
        DriveText = DriveRoot is { } root && names.TryGetValue(root.TrimEnd('\\'), out string? name)
            ? name : DriveRoot?.TrimEnd('\\') ?? "Not reported";

    private static string? InstallDriveRoot(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        try
        {
            string path = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) ? Path.GetPathRoot(path) : null;
        }
        catch (ArgumentException) { return null; }
    }

    // Checked once per row: the record never changes, and bindings re-read this while the list scrolls.
    public bool HasLocation => _hasLocation ??= !string.IsNullOrWhiteSpace(Record.InstallLocation) && Directory.Exists(Record.InstallLocation);

    public bool CanRemove => Record.Uninstall.Kind != "notAvailable";

    public string RemoveBlockedReason => Record.Uninstall.Reason ?? "This app can't be removed here.";

    public string UninstallDetail => Record.Uninstall.Kind switch
    {
        "msi" => $"Windows Installer ({Record.Uninstall.ProductCode})",
        "msix" => $"Package manager ({Record.Uninstall.PackageFullName})",
        "registeredCommand" => Record.Uninstall.Command ?? "",
        _ => Record.Uninstall.Reason ?? "None",
    };

    public string IdentifierText => Record.MsiProductCode ?? Record.PackageFamilyName ?? Record.Id;

    /// <summary>Used by UI Automation as the list item's accessible name.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Record.Publisher) ? Name : $"{Name}, {Record.Publisher}";

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Loads the icon when the row is shown. Icons are kept for the most recently shown rows only, so a long
    /// scroll through a large inventory does not keep every bitmap in memory. Rows that scroll away lose their
    /// icon and load it again when they are shown.
    /// </summary>
    public async void EnsureIcon()
    {
        if (_iconNode is not null)
        {
            IconsInMemory.Remove(_iconNode);
            IconsInMemory.AddLast(_iconNode);
            return;
        }
        if (_iconLoading)
        {
            return;
        }
        _iconLoading = true;
        ImageSource? icon = await IconLoader.LoadAsync(Record);
        _iconLoading = false;
        Icon = icon;
        _iconNode = IconsInMemory.AddLast(this);
        TrimIcons();
    }

    private static void TrimIcons()
    {
        // Selected rows keep their icons so the selection strip in the sidebar stays complete.
        IReadOnlyList<AppItemViewModel> selected = ApplicationsViewModel.Shared.SelectedItems;
        LinkedListNode<AppItemViewModel>? node = IconsInMemory.First;
        while (IconsInMemory.Count > MaxIconsInMemory && node is not null)
        {
            LinkedListNode<AppItemViewModel>? next = node.Next;
            if (!selected.Contains(node.Value))
            {
                AppItemViewModel item = node.Value;
                IconsInMemory.Remove(node);
                item._iconNode = null;
                item.Icon = null;
                // A bitmap's native pixels are released when its managed wrapper is collected. With a small managed
                // heap a scroll may not trigger a collection on its own, so collect after each batch of evictions.
                if (++_evictedSinceCollect >= EvictionsPerCollection)
                {
                    _evictedSinceCollect = 0;
                    GC.Collect();
                }
            }
            node = next;
        }
    }

    internal static string? FormatSize(ulong? bytes)
    {
        if (bytes is not { } value || value == 0)
        {
            return null;
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size >= 100 || unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in text)
            {
                hash = hash * 31 + char.ToUpperInvariant(c);
            }
            return hash;
        }
    }
}
