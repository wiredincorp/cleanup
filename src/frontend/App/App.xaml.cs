using Cleanup.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;

namespace Cleanup;

public partial class App : Application
{
    private const uint MessageBoxIconError = 0x10, MessageBoxIconInformation = 0x40, MessageBoxSetForeground = 0x10000;
    private const int ShowNormal = 5, Restore = 9;
    private static readonly TimeSpan RunningInstanceWait = TimeSpan.FromSeconds(15);

    private Mutex? _instanceMutex;
    // Shares the main window handle with later launches, whatever their file name.
    private MemoryMappedFile? _windowHandleMap;
    private static bool _selfTest;
    private static int _failureReported;

    [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);

    public App()
    {
        // Unpackaged builds otherwise find their resources through the executable's
        // file name, so a renamed download such as "Cleanup (1).exe" failed to parse
        // any XAML. Point XAML at the app's PRI file explicitly instead.
        string resources = Path.Combine(AppContext.BaseDirectory, "Cleanup.pri");
        if (!AppEnvironment.IsPackaged && File.Exists(resources))
        {
            ResourceManagerRequested += (_, args) =>
                args.CustomResourceManager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(resources);
        }
        InitializeComponent();
        UnhandledException += (_, args) => ReportFailure(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) ReportFailure(error);
        };
    }

    public static Window? MainWindow { get; private set; }

    public static bool IsAdministrator { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static string WindowHandleMapName => $"Local\\{AppEnvironment.InstanceName}.MainWindowHandle";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (RecentActivity.ExportBackgroundLog(Environment.GetCommandLineArgs()))
        {
            Exit();
            return;
        }
        try { LaunchWindow(); }
        catch (Exception error)
        {
            ReportFailure(error);
            Exit();
        }
    }

    private static string? RecordFailure(Exception error)
    {
        try
        {
            string path = Path.Combine(AppEnvironment.LocalStateDirectory, "last-error.txt");
            File.WriteAllText(path, $"{DateTime.UtcNow:O}\n{error}");
            return path;
        }
        catch (Exception) { return null; /* Preserve the original failure if diagnostics cannot be written. */ }
    }

    /// <summary>Records a fatal error and tells the person, so a failed launch is never silent.</summary>
    private static void ReportFailure(Exception error)
    {
        string? path = RecordFailure(error);
        if (_selfTest || Interlocked.Exchange(ref _failureReported, 1) == 1) return;
        string details = path is null ? "" : $"\n\nDetails were saved to:\n{path}";
        MessageBox(0, $"cleanup ran into a problem and needs to close.\n\n{error.Message}{details}", "cleanup", MessageBoxIconError | MessageBoxSetForeground);
    }

    private void LaunchWindow()
    {
        var options = SelfTestOptions.Parse(Environment.GetCommandLineArgs());
        _selfTest = options.Enabled || options.UiSmoke;
        if (options.UiSmoke && (options.Enabled || options.LocalAppDataRoot is null || options.ReportPath is null || options.FixtureDirectory is null))
            throw new ArgumentException("--ui-smoke requires --local-app-data-root, --report, and --fixture-dir, and cannot be combined with --selftest.");
        if ((options.Enabled || options.UiSmoke) && options.LocalAppDataRoot is { } localAppDataRoot)
        {
            AppEnvironment.ConfigureLocalAppDataRootForSelfTest(localAppDataRoot);
        }

        if (!options.Enabled && !options.UiSmoke && SettingsService.Current.RunAsAdministrator && !IsAdministrator
            && !Environment.GetCommandLineArgs().Any(argument => argument.Equals("--elevate-restart", StringComparison.OrdinalIgnoreCase))
            && TryStartElevated())
        {
            Exit();
            return;
        }

        if (!options.Enabled)
        {
            bool elevateRestart = Environment.GetCommandLineArgs().Any(argument => argument.Equals("--elevate-restart", StringComparison.OrdinalIgnoreCase));
            _instanceMutex = new Mutex(false, $"Local\\{AppEnvironment.InstanceName}.MainWindow");
            try
            {
                if (!_instanceMutex.WaitOne(elevateRestart ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
                {
                    ActivateRunningInstance();
                    Exit(); return;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous instance closed without releasing the mutex. This launch owns it now.
            }
            ExtractionCache.RemoveStaleInBackground();
        }
        // The feasibility window is only used by the automated test harness.
        MainWindow = options.Enabled ? new FeasibilityWindow(options) : new MainWindow();
        if (options.UiSmoke && MainWindow is MainWindow normal) normal.ConfigureReleaseSmoke(options);
        MainWindow.Activate();
        if (!options.Enabled) PublishWindowHandle(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow));
    }

    /// <summary>Starts an elevated copy. Returns false when Windows approval is cancelled.</summary>
    public static bool TryStartElevated()
    {
        string program = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (program.Length == 0)
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--elevate-restart",
            });
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    /// <summary>Restarts elevated after Windows approval. A cancelled prompt leaves this copy open.</summary>
    public static void RestartElevated()
    {
        if (!TryStartElevated())
        {
            return;
        }
        if (Current is App app)
        {
            try
            {
                app._instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released.
            }
            app._instanceMutex?.Dispose();
            app._instanceMutex = null;
        }
        Current.Exit();
    }

    private void PublishWindowHandle(nint window)
    {
        try
        {
            _windowHandleMap = MemoryMappedFile.CreateOrOpen(WindowHandleMapName, sizeof(long));
            using MemoryMappedViewAccessor view = _windowHandleMap.CreateViewAccessor();
            view.Write(0, (long)window);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Later launches fall back to finding the window by process name.
        }
    }

    /// <summary>
    /// Brings the running copy forward. The first launch may still be unpacking
    /// or creating its window, so wait for it rather than exiting silently.
    /// </summary>
    private static void ActivateRunningInstance()
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < RunningInstanceWait)
        {
            if (FindRunningWindow() is { } window)
            {
                ShowWindow(window, IsIconic(window) ? Restore : ShowNormal);
                SetForegroundWindow(window);
                return;
            }
            Thread.Sleep(250);
        }
        MessageBox(0,
            "cleanup is already running, but its window hasn't opened yet.\n\nGive it a moment. If nothing appears, close Cleanup in Task Manager and open it again.",
            "cleanup", MessageBoxIconInformation | MessageBoxSetForeground);
    }

    private static nint? FindRunningWindow()
    {
        try
        {
            using MemoryMappedFile map = MemoryMappedFile.OpenExisting(WindowHandleMapName, MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor view = map.CreateViewAccessor(0, sizeof(long), MemoryMappedFileAccess.Read);
            nint window = (nint)view.ReadInt64(0);
            if (window != 0 && IsWindow(window)) return window;
        }
        catch (Exception error) when (error is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Not published yet, or published by an older version that predates the shared handle.
        }

        // Older versions only expose their window through the process. A copy
        // downloaded again may be saved as "Cleanup (1).exe", so check both names.
        string ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Cleanup";
        foreach (string name in new[] { "Cleanup", ownName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != 0) return process.MainWindowHandle;
                }
            }
        }
        return null;
    }
}
