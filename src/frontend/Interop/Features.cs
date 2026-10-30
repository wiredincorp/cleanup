using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cleanup.Services;

namespace Cleanup.Interop;

// ---- Organizer -------------------------------------------------------------

public sealed class KnownLocations
{
    public string? Downloads { get; init; }
    public string? Desktop { get; init; }
    public string? Documents { get; init; }
    public string? Pictures { get; init; }
    public string? Videos { get; init; }
}

public sealed class OrganizePlan
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public PlannedOperation[] Operations { get; init; } = [];
    public SkippedFile[] Skipped { get; init; } = [];
    public ulong TotalBytes { get; init; }
    public int FolderCount { get; init; }
    public int RenamedCount { get; init; }
    /// <summary>Files a photo or video sort leaves alone because they are another kind of file.</summary>
    public int OtherFiles { get; init; }
}

public sealed class PlannedOperation
{
    public override string ToString() => $"{FileName}, {Source} to {Destination}";
    public FileIdentityInfo? Identity { get; init; }
    public uint Id { get; init; }
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DestinationFolder { get; init; } = "";
    public ulong SizeBytes { get; init; }
    public string Modified { get; init; } = "";
    /// <summary>Date taken or recorded (YYYY-MM-DD) when a photo or video sort read one.</summary>
    public string? Captured { get; init; }
    public bool Renamed { get; init; }
}

public sealed class SkippedFile
{
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class FailureInfo
{
    public string FileName { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class OrganizeApplyResult
{
    public string BatchId { get; init; } = "";
    public int Moved { get; init; }
    public int Failed { get; init; }
    public FailureInfo[] Failures { get; init; } = [];
}

public sealed class UndoResult
{
    public int Restored { get; init; }
    public FailureInfo[] Conflicts { get; init; } = [];
}

public sealed record FileIdentityInfo(ulong SizeBytes, ulong ModifiedTicks, uint? Volume, ulong? FileIndex);

internal sealed record OrganizePlanRequest(string Source, string GroupBy, string DateFolders, string? Destination, string? MatchExtension, string? MatchCategory, bool SkipCollisions, string? Media);

internal sealed record ApplyOperation(uint Id, string Source, string Destination, FileIdentityInfo? Identity);

internal sealed record OrganizeApplyRequest(string Source, string Destination, string Title, string JournalDirectory, ApplyOperation[] Operations);

internal sealed record UndoRequest(string JournalDirectory, string BatchId);

// ---- Leftovers -------------------------------------------------------------

public sealed record LeftoverAppInfo(
    string DisplayName,
    string? Publisher,
    string? InstallLocation,
    string Technology,
    string[] OtherInstalledNames,
    string[] OtherInstallLocations);

public sealed class LeftoverCandidate
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string[] Reasons { get; init; } = [];
    public ulong? SizeBytes { get; init; }
}

public sealed class LeftoverScanResult
{
    public LeftoverCandidate[] Candidates { get; init; } = [];
    public string? Note { get; init; }
}

internal sealed record LeftoverRemoveRequest(LeftoverAppInfo App, string[] Ids, string JournalDirectory, long OwnerWindow);

public sealed class LeftoverOutcome
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? Detail { get; init; }
}

public sealed class LeftoverRemoveResult
{
    public string BatchId { get; init; } = "";
    public int Removed { get; init; }
    public int Failed { get; init; }
    public LeftoverOutcome[] Results { get; init; } = [];
}

// ---- History ---------------------------------------------------------------

public sealed class HistoryItem
{
    public string Label { get; init; } = "";
    public string? Detail { get; init; }
    public string Outcome { get; init; } = "";
}

public sealed class HistoryEntry
{
    public string BatchId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public ulong StartedAtUnixMs { get; init; }
    public string Status { get; init; } = "";
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Undone { get; init; }
    public bool CanUndo { get; init; }
    public string? Message { get; init; }
    public HistoryItem[] Items { get; init; } = [];
}

internal sealed record JournalRequest(string JournalDirectory, string? Before = null);

internal sealed record RecordUninstallRequest(string JournalDirectory, string AppName, string? Publisher, string Status, string Message);

internal sealed record EmptyRequest;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KnownLocations))]
[JsonSerializable(typeof(OrganizePlan))]
[JsonSerializable(typeof(OrganizePlanRequest))]
[JsonSerializable(typeof(OrganizeApplyRequest))]
[JsonSerializable(typeof(OrganizeApplyResult))]
[JsonSerializable(typeof(UndoRequest))]
[JsonSerializable(typeof(UndoResult))]
[JsonSerializable(typeof(LeftoverAppInfo))]
[JsonSerializable(typeof(LeftoverScanResult))]
[JsonSerializable(typeof(LeftoverRemoveRequest))]
[JsonSerializable(typeof(LeftoverRemoveResult))]
[JsonSerializable(typeof(HistoryEntry[]))]
[JsonSerializable(typeof(JournalRequest))]
[JsonSerializable(typeof(RecordUninstallRequest))]
[JsonSerializable(typeof(EmptyRequest))]
internal sealed partial class FeatureJsonContext : JsonSerializerContext;

/// <summary>Typed, asynchronous access to the organizer, leftover, and history exports.
/// Every call runs on a thread-pool thread, never the UI thread.</summary>
internal static unsafe class Features
{
    public static string JournalDirectory { get; } = Path.Combine(AppEnvironment.LocalStateDirectory, "history");

    private static FeatureJsonContext Json => FeatureJsonContext.Default;

    public static Task<KnownLocations> GetLocationsAsync() =>
        CallAsync(new EmptyRequest(), Json.EmptyRequest, Json.KnownLocations, &NativeMethods.cln_organize_locations);

    /// <param name="media">"photos" or "videos" to sort only that kind of file by capture date; requires "date" grouping.</param>
    public static Task<OrganizePlan> PlanAsync(string source, string groupBy, string dateFolders, string? destination = null, string? extension = null, string? category = null, bool skipCollisions = false, string? media = null, CancellationToken cancellation = default) =>
        OperationAsync(new OrganizePlanRequest(source, groupBy, dateFolders, destination, extension, category, skipCollisions, media), Json.OrganizePlanRequest, Json.OrganizePlan, (nint)(delegate*<byte*, nuint, out OperationHandle, NativeBuffer*, int>)&NativeMethods.cln_organize_plan_start, cancellation);

    public static Task<OrganizeApplyResult> ApplyAsync(OrganizePlan plan, IEnumerable<PlannedOperation> operations, string title, CancellationToken cancellation = default) =>
        OperationAsync(
            new OrganizeApplyRequest(plan.Source, plan.Destination, title, JournalDirectory, operations.Select(o => new ApplyOperation(o.Id, o.Source, o.Destination, o.Identity)).ToArray()),
            Json.OrganizeApplyRequest,
            Json.OrganizeApplyResult,
            (nint)(delegate*<byte*, nuint, out OperationHandle, NativeBuffer*, int>)&NativeMethods.cln_organize_apply_start, cancellation);

    public static Task<UndoResult> UndoAsync(string batchId) =>
        CallAsync(new UndoRequest(JournalDirectory, batchId), Json.UndoRequest, Json.UndoResult, &NativeMethods.cln_organize_undo);

    public static Task<LeftoverScanResult> ScanLeftoversAsync(LeftoverAppInfo app) =>
        CallAsync(app, Json.LeftoverAppInfo, Json.LeftoverScanResult, &NativeMethods.cln_leftovers_scan);

    public static Task<LeftoverRemoveResult> RemoveLeftoversAsync(LeftoverAppInfo app, string[] ids, nint ownerWindow) =>
        CallAsync(new LeftoverRemoveRequest(app, ids, JournalDirectory, ownerWindow), Json.LeftoverRemoveRequest, Json.LeftoverRemoveResult, &NativeMethods.cln_leftovers_remove);

    public static Task<HistoryEntry[]> GetHistoryAsync(string? before = null) =>
        CallAsync(new JournalRequest(JournalDirectory, before), Json.JournalRequest, Json.HistoryEntryArray, &NativeMethods.cln_history_list);

    public static Task RecordUninstallAsync(string appName, string? publisher, string status, string message) =>
        CallAsync(new RecordUninstallRequest(JournalDirectory, appName, publisher, status, message), Json.RecordUninstallRequest, Json.EmptyRequest, &NativeMethods.cln_history_record_uninstall);

    private static Task<TResponse> CallAsync<TRequest, TResponse>(
        TRequest request,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        delegate*<byte*, nuint, NativeBuffer*, int> function)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, requestInfo);
        nint pointer = (nint)function;
        return Task.Run(() =>
        {
            string json = NativeCore.Call(payload, (delegate*<byte*, nuint, NativeBuffer*, int>)pointer);
            return JsonSerializer.Deserialize(json, responseInfo)
                ?? throw new NativeCoreException("emptyResponse", "The native core returned no data.", NativeStatus.Failed);
        });
    }

    private static Task<TResponse> OperationAsync<TRequest, TResponse>(TRequest request, JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo, nint function, CancellationToken cancellation) =>
        NativeCore.RunFeatureAsync(JsonSerializer.SerializeToUtf8Bytes(request, requestInfo), function, cancellation)
            .ContinueWith(task => JsonSerializer.Deserialize(task.GetAwaiter().GetResult(), responseInfo)
                ?? throw new NativeCoreException("emptyResponse", "The native core returned no data.", NativeStatus.Failed), TaskScheduler.Default);
}
