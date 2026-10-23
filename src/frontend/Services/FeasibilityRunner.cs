using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Cleanup.Interop;
using Microsoft.Win32;

namespace Cleanup.Services;

/// <summary>
/// Phase 0 feasibility checks, run from inside the installed package. Each
/// section is isolated so one failure is recorded without hiding the others.
/// </summary>
internal sealed class FeasibilityRunner(SelfTestOptions options, nint ownerWindow, Action<string> log)
{
    /// <summary>
    /// Only applications registered by the disposable test fixtures may be
    /// uninstalled by the feasibility run.
    /// </summary>
    public const string FixturePublisher = "Cleanup Test Fixtures";

    public static bool IsFixture(AppRecord app) =>
        app.Publisher == FixturePublisher
        && app.Id is "reg:hkcu:shared:CleanupDisposableTestApp.User"
            or "reg:hklm:64:CleanupDisposableTestApp.Machine"
            or "msi:user:{6E1C3A52-7B1D-4F0A-9C3E-2D5B8A41F7C9}"
            or "msix:Cleanup.DisposableTestPackage_nk839fbkkjhj8";

    public async Task<JsonObject> RunAsync(double startupToLoadedMs)
    {
        var errors = new JsonArray();
        var report = new JsonObject
        {
            ["reportVersion"] = 1,
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["machine"] = Machine(),
            ["process"] = new JsonObject
            {
                ["packaged"] = AppEnvironment.IsPackaged,
                ["portableSingleFile"] = AppEnvironment.IsPortableSingleFile,
                ["packageFullName"] = AppEnvironment.PackageFullName,
                ["installedPath"] = AppEnvironment.InstalledPath,
                ["localStateDirectory"] = AppEnvironment.LocalStateDirectory,
                ["processPath"] = Environment.ProcessPath,
                ["startupToWindowLoadedMs"] = Math.Round(startupToLoadedMs),
            },
        };

        await Section(report, errors, "idle", async () => await MeasureIdleAsync());

        bool coreLoaded = await Section(report, errors, "nativeCore", () =>
        {
            NativeCore.EnsureCompatible();
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["abiVersion"] = NativeCore.AbiVersion,
                ["environment"] = JsonNode.Parse(NativeCore.ProbeEnvironment()),
            });
        });
        if (!coreLoaded)
        {
            report["errors"] = errors;
            return report;
        }

        List<AppRecord> fixtures = [];
        await Section(report, errors, "inventory", async () =>
        {
            log("Scanning installed applications...");
            var stopwatch = Stopwatch.StartNew();
            InventoryScanResult scan = await NativeCore.ScanInventoryAsync(null, CancellationToken.None);
            stopwatch.Stop();
            fixtures = scan.Records.Where(IsFixture).ToList();
            log($"Found {scan.Records.Count} applications in {stopwatch.ElapsedMilliseconds} ms; {fixtures.Count} test fixtures.");
            return new JsonObject
            {
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["records"] = scan.Records.Count,
                ["batches"] = scan.Batches,
                ["summary"] = JsonNode.Parse(scan.SummaryJson),
                ["sample"] = new JsonArray(scan.Records.Take(12).Select(r => (JsonNode)Describe(r)).ToArray()),
                ["fixtures"] = new JsonArray(fixtures.Select(r => (JsonNode)Describe(r)).ToArray()),
                ["workingSetAfterScanMb"] = WorkingSetMb(),
            };
        });

        if (options.UninstallFixtures)
        {
            await Section(report, errors, "uninstall", async () =>
            {
                var results = new JsonArray();
                // Sequential: one official uninstaller at a time.
                foreach (AppRecord app in fixtures)
                {
                    log($"Removing {app.DisplayName} ({app.Id})...");
                    string json = await NativeCore.UninstallAsync(
                        app.Id,
                        options.AllowElevation,
                        ownerWindow,
                        stage => log($"  {app.DisplayName}: {stage}"),
                        CancellationToken.None);
                    JsonNode? result = JsonNode.Parse(json);
                    log($"  {app.DisplayName}: {result?["status"]} - {result?["message"]}");
                    results.Add(new JsonObject { ["displayName"] = app.DisplayName, ["report"] = result });
                }
                InventoryScanResult after = await NativeCore.ScanInventoryAsync(null, CancellationToken.None);
                return new JsonObject
                {
                    ["results"] = results,
                    ["fixturesRemainingAfterRescan"] = new JsonArray(after.Records.Where(IsFixture).Select(r => (JsonNode)Describe(r)).ToArray()),
                };
            });
        }

        await Section(report, errors, "persistence", () =>
            Task.Run(() => JsonNode.Parse(NativeCore.ProbePersistence(AppEnvironment.LocalStateDirectory))));

        if (options.FixtureDirectory is { } fixtureDirectory)
        {
            await Section(report, errors, "fileMoves", () => Task.Run(() =>
            {
                Directory.CreateDirectory(fixtureDirectory);
                return JsonNode.Parse(NativeCore.ProbeFileMoves(fixtureDirectory));
            }));
        }

        report["memoryAtEnd"] = new JsonObject
        {
            ["workingSetMb"] = WorkingSetMb(),
            ["privateMb"] = Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, 1),
        };
        report["errors"] = errors;
        return report;
    }

    private async Task<bool> Section(JsonObject report, JsonArray errors, string name, Func<Task<JsonNode?>> body)
    {
        try
        {
            report[name] = await body();
            return true;
        }
        catch (Exception e)
        {
            log($"{name} failed: {e.Message}");
            errors.Add(new JsonObject
            {
                ["section"] = name,
                ["type"] = e.GetType().FullName,
                ["message"] = e.Message,
                ["code"] = (e as NativeCoreException)?.Code,
            });
            return false;
        }
    }

    /// <summary>Lets startup settle, then samples CPU and memory while idle.</summary>
    private async Task<JsonNode?> MeasureIdleAsync()
    {
        const int settleMs = 3000;
        const int sampleMs = 5000;
        log("Measuring idle CPU and memory...");
        await Task.Delay(settleMs);
        Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(sampleMs);
        process.Refresh();
        double cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds
            / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        return new JsonObject
        {
            ["settleMs"] = settleMs,
            ["sampleMs"] = sampleMs,
            ["cpuPercentOfMachine"] = Math.Round(cpuPercent, 3),
            ["workingSetMb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
            ["privateMb"] = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
        };
    }

    private static JsonObject Describe(AppRecord app) => new()
    {
        ["id"] = app.Id,
        ["displayName"] = app.DisplayName,
        ["publisher"] = app.Publisher,
        ["version"] = app.Version,
        ["technology"] = app.Technology,
        ["scope"] = app.Scope,
        ["uninstall"] = app.Uninstall.Kind,
    };

    private static double WorkingSetMb()
    {
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        return Math.Round(process.WorkingSet64 / 1048576.0, 1);
    }

    private static JsonObject Machine()
    {
        string? cpu = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            null) as string;
        return new JsonObject
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["osBuild"] = Environment.OSVersion.Version.ToString(),
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["cpu"] = cpu?.Trim(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["memoryGb"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
        };
    }
}
