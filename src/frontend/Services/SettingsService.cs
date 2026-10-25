using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cleanup.Services;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public enum BackdropKind
{
    Acrylic,
    Mica,
    Solid,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;

    /// <summary>"system" preserves native defaults; other presets enable a custom acrylic tint.</summary>
    public string AcrylicTintPreset { get; set; } = "system";
    public string AcrylicTintColor { get; set; } = "#36264D";
    public double AcrylicTintStrength { get; set; } = 65;
    public double AcrylicBackgroundVisibility { get; set; } = 40;

    /// <summary>Shows history diagnostics. Never changes confirmations or privileges.</summary>
    public bool AdvancedMode { get; set; }

    /// <summary>When true, the next launch asks Windows to start cleanup as administrator.</summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>When false, the Windows accent color is used and followed live.</summary>
    public bool UseCustomAccent { get; set; }

    /// <summary>Custom accent as #RRGGBB.</summary>
    public string CustomAccent { get; set; } = "#3B82F6";

    /// <summary>Folder last previewed on the Organize page.</summary>
    public string? LastOrganizeFolder { get; set; }

    /// <summary>Organize mode: "simple", "type", "extension", or "date".</summary>
    public string OrganizeMode { get; set; } = "simple";

    /// <summary>Date subfolders: "none", "year", "month", or "yearMonth".</summary>
    public string OrganizeDateFolders { get; set; } = "none";

    /// <summary>When a file name is already taken: "ask", "keepBoth", or "skip".</summary>
    public string OrganizeCollisions { get; set; } = "ask";

    /// <summary>
    /// Folders searched for emulators and their game files, such as a folder that holds your emulators. Steam and
    /// Epic Games libraries are found without this list.
    /// </summary>
    public List<string> GameFolders { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and saves user preferences in the app's LocalState folder.</summary>
public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(AppEnvironment.LocalStateDirectory, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>Raised on the UI thread after any setting changes.</summary>
    public static event Action? Changed;

    public static void Update(Action<AppSettings> change)
    {
        Remember(change);
        Changed?.Invoke();
    }

    /// <summary>Saves a non-visual preference without re-applying the appearance.</summary>
    public static void Remember(Action<AppSettings> change)
    {
        change(Current);
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Preferences are best-effort; the change still applies for this session.
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }
}
