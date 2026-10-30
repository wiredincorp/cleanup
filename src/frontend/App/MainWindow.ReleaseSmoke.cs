using Cleanup.Interop;
using Cleanup.Services;
using Cleanup.Views;
using System.Diagnostics;
using System.Text.Json.Nodes;
namespace Cleanup;

public sealed partial class MainWindow
{
    internal void ConfigureReleaseSmoke(SelfTestOptions options)
    {
        RootGrid.Loaded += async (_, _) =>
        {
            var report = new JsonObject { ["normalMainWindow"] = true, ["abiVersion"] = NativeCore.AbiVersion };
            try
            {
                NativeCore.EnsureCompatible();
                var samples = new JsonArray();
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    foreach (Type page in new[] { typeof(ApplicationsPage), typeof(OrganizePage), typeof(PhotosPage), typeof(VideosPage), typeof(CorePage), typeof(HistoryPage), typeof(SettingsPage) })
                    {
                        await NavigateAsync(page);
                        await Task.Delay(250);
                        if (ContentFrame.CurrentSourcePageType != page) throw new InvalidOperationException($"Navigation failed: {page.Name}");
                    }
                    // Diagnostic collection distinguishes retained objects from
                    // short-lived XAML/COM wrappers awaiting normal collection.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(100);
                    using var process = Process.GetCurrentProcess();
                    samples.Add(new JsonObject { ["privateBytes"] = process.PrivateMemorySize64, ["managedBytes"] = GC.GetTotalMemory(false), ["handles"] = process.HandleCount, ["threads"] = process.Threads.Count });
                }
                report["navigationCycles"] = 10;
                report["samples"] = samples;
                report["organizer"] = await OrganizerSmoke.RunAsync(options.FixtureDirectory!);
                report["photos"] = await OrganizerSmoke.RunPhotosAsync(options.FixtureDirectory!);
                if (File.Exists(Path.Combine(AppEnvironment.LocalStateDirectory, "last-error.txt"))) throw new InvalidOperationException("The normal application reported an unhandled error. Inspect last-error.txt in the isolated state folder.");
                report["passed"] = true;
            }
            catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
            try
            {
                string path = Path.GetFullPath(options.ReportPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, report.ToJsonString());
            }
            finally { Close(); }
        };
    }
}
