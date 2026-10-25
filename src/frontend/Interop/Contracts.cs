using System.Text.Json.Serialization;

namespace Cleanup.Interop;

/// <summary>Normalized application record produced by the native core.</summary>
public sealed class AppRecord
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string Technology { get; init; } = "";
    public string Scope { get; init; } = "";
    public string? InstallLocation { get; init; }
    public string? InstallDate { get; init; }
    public ulong? EstimatedSizeBytes { get; init; }
    public string? IconSource { get; init; }
    public UninstallMethodInfo Uninstall { get; init; } = new();
    public string? RegistryKey { get; init; }
    public string? MsiProductCode { get; init; }
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }

    /// <summary>When a store or game library recorded a launch. Not part of the native inventory.</summary>
    [JsonIgnore]
    public DateTime? LastRun { get; init; }

    [JsonIgnore]
    public string Summary =>
        string.Join("  ·  ", new[] { Publisher, Version, $"{Technology} ({Scope})" }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed class UninstallMethodInfo
{
    public string Kind { get; init; } = "";
    public string? ProductCode { get; init; }
    public string? PackageFullName { get; init; }
    public string? Command { get; init; }
    public string? Reason { get; init; }
}

internal sealed class CoreErrorInfo
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public uint? Win32Error { get; init; }
}

internal sealed record DirectoryRequest(string Directory);

internal sealed record ScanRequest(bool IncludeMsix, int BatchSize);

internal sealed record UninstallRequest(string AppId, bool AllowElevation, long OwnerWindow);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppRecord[]))]
[JsonSerializable(typeof(CoreErrorInfo))]
[JsonSerializable(typeof(DirectoryRequest))]
[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(UninstallRequest))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
