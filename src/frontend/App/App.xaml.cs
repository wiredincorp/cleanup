using Cleanup.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Cleanup;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint window, int command);
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => RecordFailure(args.Exception);
    }

    public static Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { LaunchWindow(); }
        catch (Exception error) { RecordFailure(error); throw; }
    }

    private static void RecordFailure(Exception error)
    {
        try { File.WriteAllText(Path.Combine(AppEnvironment.LocalStateDirectory, "last-error.txt"), $"{DateTime.UtcNow:O}\n{error}"); }
        catch (Exception) { /* Preserve the original failure if diagnostics cannot be written. */ }
    }

    private void LaunchWindow()
    {
        var options = SelfTestOptions.Parse(Environment.GetCommandLineArgs());
        if (options.Enabled && options.LocalAppDataRoot is { } localAppDataRoot)
        {
            AppEnvironment.ConfigureLocalAppDataRootForSelfTest(localAppDataRoot);
        }

        if (!options.Enabled)
        {
            _instanceMutex = new Mutex(true, $"Local\\{AppEnvironment.InstanceName}.MainWindow", out bool ownsInstance);
            if (!ownsInstance)
            {
                // A downloaded copy may be saved as "Cleanup (1).exe"; match this process's own name.
                string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Cleanup";
                foreach (Process process in Process.GetProcessesByName(processName).Where(process => process.Id != Environment.ProcessId))
                {
                    if (process.MainWindowHandle == 0) continue;
                    ShowWindow(process.MainWindowHandle, 9); SetForegroundWindow(process.MainWindowHandle); break;
                }
                Exit(); return;
            }
            ExtractionCache.RemoveStaleInBackground();
        }
        // The feasibility window is only used by the automated test harness.
        MainWindow = options.Enabled ? new FeasibilityWindow(options) : new MainWindow();
        MainWindow.Activate();
    }
}
