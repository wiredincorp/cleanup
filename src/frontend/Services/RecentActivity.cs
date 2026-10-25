using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace Cleanup.Services;

/// <summary>
/// Which installed programs are running, and when Windows last executed them.
/// A normal account only has a short shell history. The background activity log
/// has the rest, and Windows allows only an administrator to read it.
/// </summary>
internal static class RecentActivity
{
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    internal readonly record struct RunHit(bool Running, DateTime? LastRan);

    internal sealed class Snapshot
    {
        public static Snapshot Empty { get; } = new([], new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Snapshot(IReadOnlyList<string> runningPaths, IReadOnlyDictionary<string, DateTime> lastRan, IReadOnlySet<string> runningNames)
        {
            RunningPaths = runningPaths;
            LastRan = lastRan;
            RunningNames = runningNames;
        }

        public IReadOnlyList<string> RunningPaths { get; }
        public IReadOnlyDictionary<string, DateTime> LastRan { get; }
        public IReadOnlySet<string> RunningNames { get; }

        /// <summary>True when the administrator activity log was part of this snapshot.</summary>
        public bool IncludesBackgroundLog { get; init; }

        public RunHit Match(string? installLocation, string? executable, string? packageFamily = null, string? displayName = null) =>
            RecentActivity.Match(RunningPaths, LastRan, RunningNames, installLocation, executable, packageFamily, displayName);
    }

    internal enum BackgroundLogResult { Unavailable, Written, Declined }

    [SupportedOSPlatform("windows")]
    public static Snapshot Read()
    {
        var runningPaths = new List<string>();
        var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Length > 0)
                    {
                        runningNames.Add(process.ProcessName);
                    }
                    try
                    {
                        string? file = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file))
                        {
                            runningPaths.Add(file);
                        }
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    {
                        // Protected and elevated processes still contribute their name above.
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being read.
                }
            }
        }

        var lastRan = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        ReadExecutions(lastRan);
        ReadStoreUsage(lastRan);
        bool background = ReadBackgroundExecutions(lastRan);
        return new Snapshot(runningPaths, lastRan, runningNames) { IncludesBackgroundLog = background };
    }

    /// <summary>Writes the administrator activity log and exits. Used by the elevated helper.</summary>
    [SupportedOSPlatform("windows")]
    public static bool ExportBackgroundLog(string[] args)
    {
        int index = Array.FindIndex(args, argument => argument.Equals("--export-run-history", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }
        string destination = Path.GetFullPath(args[index + 1]);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!destination.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var lastRan = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        ReadBackgroundExecutions(lastRan);
        var lines = new List<string>(lastRan.Count);
        foreach ((string path, DateTime when) in lastRan)
        {
            lines.Add(when.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + path.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
        }
        File.WriteAllLines(destination, lines);
        return true;
    }

    [SupportedOSPlatform("windows")]
    public static BackgroundLogResult RequestBackgroundLog(string destination)
    {
        string full = Path.GetFullPath(destination);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundLogResult.Unavailable;
        }
        try
        {
            File.Delete(full);
        }
        catch (IOException)
        {
            // A previous file is overwritten by the helper when it can.
        }
        string program = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (program.Length == 0)
        {
            return BackgroundLogResult.Unavailable;
        }
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--export-run-history \"" + full + "\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null || !process.WaitForExit(120_000) || !File.Exists(full))
            {
                return BackgroundLogResult.Unavailable;
            }
            return BackgroundLogResult.Written;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return BackgroundLogResult.Declined;
        }
    }

    public static Snapshot WithBackgroundLog(Snapshot snapshot, string file)
    {
        var lastRan = new Dictionary<string, DateTime>(snapshot.LastRan, StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(file))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line[..tab], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ticks))
            {
                continue;
            }
            try
            {
                Remember(lastRan, line[(tab + 1)..], new DateTime(ticks, DateTimeKind.Utc).ToLocalTime());
            }
            catch (ArgumentException)
            {
                // A damaged line is skipped.
            }
        }
        return new Snapshot(snapshot.RunningPaths, lastRan, snapshot.RunningNames) { IncludesBackgroundLog = true };
    }

    internal static RunHit Match(
        IEnumerable<string> runningPaths,
        IReadOnlyDictionary<string, DateTime> lastRan,
        IReadOnlySet<string> runningNames,
        string? installLocation,
        string? executable,
        string? packageFamily = null,
        string? displayName = null)
    {
        string? folder = SpecificFolder(installLocation);
        string? exeName = executable is null ? null : Path.GetFileName(executable);
        bool namedExe = exeName is not null && !GenericExecutable(exeName);

        bool running = false;
        if (executable is not null)
        {
            running = runningPaths.Any(path => path.Equals(executable, StringComparison.OrdinalIgnoreCase))
                || (namedExe && runningNames.Contains(Path.GetFileNameWithoutExtension(exeName!)));
        }
        if (!running && folder is not null)
        {
            running = runningPaths.Any(path => IsUnder(path, folder));
        }

        DateTime? ran = null;
        void Consider(DateTime when)
        {
            if (ran is null || when > ran)
            {
                ran = when;
            }
        }
        foreach ((string path, DateTime when) in lastRan)
        {
            if (executable is not null && path.Equals(executable, StringComparison.OrdinalIgnoreCase))
            {
                Consider(when);
            }
            else if (folder is not null && (IsUnder(path, folder) || path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                Consider(when);
            }
            else if (namedExe && path.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase))
            {
                Consider(when);
            }
            else if (PackageMatches(path, packageFamily))
            {
                Consider(when);
            }
            else if (!path.Contains('\\') && !path.Contains('!') && TokenEquals(path, exeName, displayName))
            {
                Consider(when);
            }
            else if (SameProgram(path, displayName))
            {
                Consider(when);
            }
        }
        return new RunHit(running, ran);
    }

    /// <summary>Status for a row, or null when this program has not run in the recent window.</summary>
    internal static string? Caption(bool running, DateTime? lastRan, DateTime now)
    {
        if (running)
        {
            return "Running";
        }
        if (lastRan is not DateTime when || when < now - RecentWindow)
        {
            return null;
        }
        if (when > now)
        {
            return "Ran just now";
        }
        TimeSpan age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Ran just now";
        }
        if (age < TimeSpan.FromHours(1))
        {
            int minutes = Math.Max(1, (int)age.TotalMinutes);
            return minutes == 1 ? "Ran 1 min ago" : $"Ran {minutes} min ago";
        }
        if (when.Date == now.Date)
        {
            int hours = Math.Max(1, (int)age.TotalHours);
            return hours == 1 ? "Ran 1 hr ago" : $"Ran {hours} hr ago";
        }
        if (when.Date == now.Date.AddDays(-1))
        {
            return "Ran yesterday";
        }
        if (age < TimeSpan.FromDays(7))
        {
            int days = Math.Max(2, (int)age.TotalDays);
            return $"Ran {days} days ago";
        }
        return "Ran " + when.ToString("MMM d");
    }

    internal static string Rot13(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= 'a' and <= 'z')
            {
                chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                chars[i] = (char)('A' + (c - 'A' + 13) % 26);
            }
        }
        return new string(chars);
    }

    /// <summary>UserAssist stores the last execution FILETIME at byte 60 of the count record.</summary>
    internal static DateTime? LastExecuted(byte[] data)
    {
        if (data.Length < 68)
        {
            return null;
        }
        long fileTime = BitConverter.ToInt64(data, 60);
        if (fileTime <= 0)
        {
            return null;
        }
        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string? ToExecutablePath(string decoded)
    {
        decoded = decoded.Trim().Trim('"');
        if (!decoded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (decoded.Length > 3 && decoded[1] == ':' && decoded[2] == '\\')
        {
            return decoded;
        }
        int end = decoded.IndexOf('}');
        if (!decoded.StartsWith('{') || end < 0)
        {
            return null;
        }
        string guid = decoded[..(end + 1)];
        string rest = decoded[(end + 1)..].TrimStart('\\');
        return Folder(guid) is { } root && rest.Length > 0 ? Path.Combine(root, rest) : null;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadExecutions(Dictionary<string, DateTime> lastRan)
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
        if (root is null)
        {
            return;
        }
        foreach (string guid in new[] { "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}", "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}" })
        {
            using RegistryKey? count = root.OpenSubKey(guid + @"\Count");
            if (count is null)
            {
                continue;
            }
            foreach (string name in count.GetValueNames())
            {
                if (count.GetValue(name) is not byte[] data || LastExecuted(data) is not DateTime when)
                {
                    continue;
                }
                string decoded = Rot13(name);
                if (ToExecutablePath(decoded) is string path)
                {
                    Remember(lastRan, path, when);
                }
                else if (decoded.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(ExpandKnownFolder(decoded)) is string target)
                {
                    Remember(lastRan, target, when);
                }
                else if (IsProgramToken(decoded))
                {
                    Remember(lastRan, decoded, when);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadBackgroundExecutions(Dictionary<string, DateTime> lastRan)
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            return false;
        }
        Dictionary<string, string> volumes = VolumeMap();
        bool opened = false;
        foreach (string service in new[] { "bam", "dam" })
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}\State\UserSettings\{sid}");
                if (key is null)
                {
                    continue;
                }
                opened = true;
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not byte[] data || ExecutionTime(data) is not DateTime when || ToDosPath(name, volumes) is not string path)
                    {
                        continue;
                    }
                    Remember(lastRan, path, when);
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // The background log stays unread until an administrator approves it.
            }
        }
        return opened;
    }

    internal static DateTime? ExecutionTime(byte[] data)
    {
        if (data.Length < 8)
        {
            return null;
        }
        DateTime? first = FileTimeAt(data, 0);
        if (first is not null)
        {
            return first;
        }
        for (int offset = 8; offset + 8 <= data.Length && offset <= 32; offset += 8)
        {
            if (FileTimeAt(data, offset) is DateTime when)
            {
                return when;
            }
        }
        return null;
    }

    internal static string? ToDosPath(string name, IReadOnlyDictionary<string, string> volumes)
    {
        if (name.Length > 3 && name[1] == ':' && name[2] == '\\')
        {
            return name;
        }
        foreach ((string device, string drive) in volumes)
        {
            if (name.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return drive + name[(device.Length + 1)..];
            }
        }
        return null;
    }

    private static void Remember(Dictionary<string, DateTime> lastRan, string path, DateTime when)
    {
        if (!lastRan.TryGetValue(path, out DateTime existing) || when > existing)
        {
            lastRan[path] = when;
        }
    }

    private static bool GenericExecutable(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return name.Length < 4 || name.Equals("setup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("update", StringComparison.OrdinalIgnoreCase)
            || name.Equals("updater", StringComparison.OrdinalIgnoreCase)
            || name.Equals("install", StringComparison.OrdinalIgnoreCase)
            || name.Equals("installer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("launcher", StringComparison.OrdinalIgnoreCase)
            || name.Equals("helper", StringComparison.OrdinalIgnoreCase)
            || name.Equals("redist", StringComparison.OrdinalIgnoreCase)
            || name.Contains("unins", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The recorded program is this app when its file or a parent folder carries the app's name.</summary>
    private static bool SameProgram(string path, string? displayName)
    {
        string simplified = Simplified(displayName);
        if (simplified.Length < 4)
        {
            return false;
        }
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = Letters(Path.GetFileNameWithoutExtension(part));
            if (stem.Length < 4)
            {
                continue;
            }
            if (stem == simplified)
            {
                return true;
            }
            if (simplified.Length >= 5 && stem.Length >= 5
                && (stem.StartsWith(simplified, StringComparison.Ordinal) || simplified.StartsWith(stem, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    private static string Simplified(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }
        var cleaned = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char c in name)
        {
            if (c is '(' or '[')
            {
                depth++;
                continue;
            }
            if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (depth == 0)
            {
                cleaned.Append(c);
            }
        }
        string[] tokens = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        while (tokens.Length > 1 && tokens[^1].Any(char.IsDigit))
        {
            tokens = tokens[..^1];
        }
        return Letters(string.Join(' ', tokens));
    }

    /// <summary>A Store package folder inserts the version between the package name and the publisher id.</summary>
    internal static bool PackageMatches(string path, string? family)
    {
        if (string.IsNullOrEmpty(family))
        {
            return false;
        }
        if (path.Contains(family, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        int split = family.LastIndexOf('_');
        if (split <= 0 || split >= family.Length - 1)
        {
            return false;
        }
        string name = family[..split];
        string publisher = family[(split + 1)..];
        return name.Length >= 3
            && path.Contains(name + "_", StringComparison.OrdinalIgnoreCase)
            && path.Contains("__" + publisher, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void ReadStoreUsage(Dictionary<string, DateTime> lastRan)
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData");
        if (root is null)
        {
            return;
        }
        foreach (string family in root.GetSubKeyNames())
        {
            using RegistryKey? apps = root.OpenSubKey(family + @"\HAM\AUI");
            if (apps is null)
            {
                continue;
            }
            DateTime? latest = null;
            foreach (string app in apps.GetSubKeyNames())
            {
                using RegistryKey? used = apps.OpenSubKey(app + @"\V1\LU");
                if (used?.GetValue("PCT") is not object value)
                {
                    continue;
                }
                try
                {
                    long fileTime = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (FileTimeAt(BitConverter.GetBytes(fileTime), 0) is DateTime when && (latest is null || when > latest))
                    {
                        latest = when;
                    }
                }
                catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
                {
                    // A usage value that is not a time is ignored.
                }
            }
            if (latest is DateTime ran)
            {
                Remember(lastRan, family, ran);
            }
        }
    }

    private static bool TokenEquals(string token, string? exeName, string? displayName)
    {
        string key = Letters(token);
        if (key.Length < 4)
        {
            return false;
        }
        if (exeName is not null && key == Letters(Path.GetFileNameWithoutExtension(exeName)))
        {
            return true;
        }
        return displayName is not null && key == Letters(displayName);
    }

    private static string Letters(string value)
    {
        var chars = new List<char>(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
            }
        }
        return new string(chars.ToArray());
    }

    private static bool IsProgramToken(string decoded) =>
        decoded.Length is >= 2 and <= 180
        && !decoded.StartsWith("UEME_", StringComparison.Ordinal)
        && !decoded.Contains('\\')
        && decoded.Any(char.IsLetter);

    private static string? ExpandKnownFolder(string decoded)
    {
        int end = decoded.IndexOf('}');
        if (!decoded.StartsWith('{') || end < 0)
        {
            return decoded.Length > 3 && decoded[1] == ':' ? decoded : null;
        }
        string? root = Folder(decoded[..(end + 1)]);
        string rest = decoded[(end + 1)..].TrimStart('\\');
        return root is not null && rest.Length > 0 ? Path.Combine(root, rest) : null;
    }

    private static string? ShortcutTarget(string? link)
    {
        if (link is null || !File.Exists(link))
        {
            return null;
        }
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [link])!;
            if (shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) is string target
                && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }
        catch (Exception error) when (error is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            // A shortcut that cannot be read is skipped.
        }
        return null;
    }

    private static DateTime? FileTimeAt(byte[] data, int offset)
    {
        long fileTime = BitConverter.ToInt64(data, offset);
        if (fileTime <= 0)
        {
            return null;
        }
        try
        {
            DateTime when = DateTime.FromFileTime(fileTime);
            return when.Year is >= 2010 and <= 2100 ? when : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> VolumeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string letter = drive.Name.TrimEnd('\\');
            var buffer = new char[1024];
            if (QueryDosDevice(letter, buffer, (uint)buffer.Length) == 0)
            {
                continue;
            }
            string device = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
            if (device.Length > 0)
            {
                map[device] = letter + "\\";
            }
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] target, uint length);

    private static string? SpecificFolder(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Trim().Trim('"')));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (full.Length <= 3)
        {
            return null;
        }
        foreach (Environment.SpecialFolder special in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
        })
        {
            string root = Environment.GetFolderPath(special);
            if (root.Length > 0 && full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return full;
    }

    private static bool IsUnder(string file, string folder) =>
        file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Folder(string guid) => guid.ToUpperInvariant() switch
    {
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" or "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}" => Environment.SystemDirectory,
        "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "{6D809377-6AF0-444B-8957-A3773F02200E}" or "{905E63B6-C1BF-494E-B29C-65B732D3D21A}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}" => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}" => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        "{9E3995AB-1F9C-4F13-B827-48B24B6C7174}" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned"),
        _ => null,
    };
}
