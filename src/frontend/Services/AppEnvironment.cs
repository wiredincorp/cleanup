using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace Cleanup.Services;

/// <summary>Package identity and writable data locations for the running app.</summary>
internal static class AppEnvironment
{
    private static readonly object StateDirectoryLock = new();
    private static string? _localAppDataRootForSelfTest;
    private static string? _localStateDirectory;

    public static string? PackageFullName { get; } = TryGetPackageFullName();

    public static bool IsPackaged => PackageFullName is not null;

    /// <summary>
    /// The portable download is a single-file bundle; the Setup app is a
    /// regular self-contained folder publish.
    /// </summary>
    public static bool IsPortableSingleFile { get; } = !IsPackaged && IsEntryAssemblyBundled();

    public static string? InstalledPath { get; } = IsPackaged ? Windows.ApplicationModel.Package.Current.InstalledPath : AppContext.BaseDirectory;
    public static string InstanceName => IsPackaged ? Windows.ApplicationModel.Package.Current.Id.FamilyName : "Cleanup.Desktop";

    /// <summary>
    /// Per-user writable state. Inside the package this is the package's
    /// LocalState folder. The portable single-file download uses CleanupData
    /// beside the EXE when that folder is writable, and otherwise falls back
    /// to %LOCALAPPDATA%\Cleanup. Setup and development builds keep their
    /// per-user state outside the install/build folder.
    /// </summary>
    public static string LocalStateDirectory
    {
        get
        {
            lock (StateDirectoryLock)
            {
                return _localStateDirectory ??= ResolveLocalState();
            }
        }
    }

    /// <summary>Isolates state used by the self-test harness from a developer's profile.</summary>
    public static void ConfigureLocalAppDataRootForSelfTest(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A self-test LocalAppData root is required.", nameof(root));
        }

        lock (StateDirectoryLock)
        {
            if (_localStateDirectory is not null)
            {
                throw new InvalidOperationException("The local state directory has already been resolved.");
            }

            _localAppDataRootForSelfTest = Path.GetFullPath(root);
        }
    }

    private static string? TryGetPackageFullName()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FullName;
        }
        catch (Exception e) when (e is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static bool IsEntryAssemblyBundled()
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly is null)
        {
            return false;
        }

        string? assemblyLocation = entryAssembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
        {
            return true;
        }

        string? assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
        string? processPath = Environment.ProcessPath;
        string? processDirectory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(assemblyDirectory) || string.IsNullOrWhiteSpace(processDirectory))
        {
            return false;
        }

        // IncludeAllContentForSelfExtract places the bundled entry assembly
        // under the .NET extraction cache, separate from the original EXE.
        return !string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(assemblyDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(processDirectory)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLocalState()
    {
        if (IsPackaged)
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }

        string userStateDirectory = GetUserStateDirectory();
#if !DEBUG
        if (IsPortableSingleFile && TryUsePortableStateDirectory(userStateDirectory) is { } portableStateDirectory)
        {
            return portableStateDirectory;
        }
#endif

        Directory.CreateDirectory(userStateDirectory);
        return userStateDirectory;
    }

    private static string GetUserStateDirectory()
    {
        string localAppData = _localAppDataRootForSelfTest
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not provide the current user's local app data folder.");
        }

#if DEBUG
        const string appDataFolderName = "Cleanup.Dev";
#else
        const string appDataFolderName = "Cleanup";
#endif
        return Path.Combine(localAppData, appDataFolderName);
    }

    private static string? TryUsePortableStateDirectory(string userStateDirectory)
    {
        string? executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return null;
        }

        string portableStateDirectory = Path.GetFullPath(Path.Combine(executableDirectory, "CleanupData"));
        bool alreadyExisted = Directory.Exists(portableStateDirectory);
        if (!CanWriteDirectory(portableStateDirectory))
        {
            if (!alreadyExisted)
            {
                DeleteEmptyDirectory(portableStateDirectory);
            }
            return null;
        }

        // The previous portable release kept settings and history in LocalAppData.
        // Copy user state once when creating a new portable data folder; keep the
        // original so a read-only drive can still fall back without data loss.
        if (!alreadyExisted && !TryMigrateUserState(userStateDirectory, portableStateDirectory))
        {
            DeleteEmptyDirectory(portableStateDirectory);
            return null;
        }

        return portableStateDirectory;
    }

    private static bool CanWriteDirectory(string directory)
    {
        string probe = Path.Combine(directory, $".cleanup-write-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
            return !File.Exists(probe);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or SecurityException)
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Best effort: the failed probe must not prevent LocalAppData fallback.
            }
            return false;
        }
    }

    private static bool TryMigrateUserState(string sourceDirectory, string portableDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return true;
        }

        string stagingDirectory = portableDirectory + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            CopyUserState(sourceDirectory, stagingDirectory);

            if (!Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            {
                Directory.Delete(stagingDirectory);
                return true;
            }

            // Do not overwrite state another Cleanup process created meanwhile.
            if (Directory.EnumerateFileSystemEntries(portableDirectory).Any())
            {
                Directory.Delete(stagingDirectory, recursive: true);
                return true;
            }

            Directory.Delete(portableDirectory);
            Directory.Move(stagingDirectory, portableDirectory);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or SecurityException)
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException)
            {
                // The original LocalAppData state remains untouched.
            }
            return false;
        }
    }

    private static void CopyUserState(string sourceDirectory, string destinationDirectory)
    {
        foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            string name = Path.GetFileName(sourcePath);
            if (name.Equals("last-error.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("performance-audit.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("phase0-persistence-probe.jsonl", StringComparison.OrdinalIgnoreCase)
                || name.Equals("probe-file-moves", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(sourcePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string destinationPath = Path.Combine(destinationDirectory, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationPath);
                CopyUserState(sourcePath, destinationPath);
            }
            else
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }
    }

    private static void DeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The fallback remains usable even if an empty folder cannot be removed.
        }
    }
}
