using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cleanup.Interop;
using Microsoft.Win32;

namespace Cleanup.Services;

/// <summary>A game found on this machine, and the way to open it.</summary>
internal sealed record GameEntry(AppRecord Record, LaunchTarget Launch);

/// <summary>
/// Finds games without changing anything: Steam and Epic Games libraries, and the games of emulators installed in the
/// folders the user chose. Each source is read on its own, so one unreadable library never hides the others.
/// </summary>
internal static class GameLibrary
{
    private static readonly TimeSpan FolderLimit = TimeSpan.FromSeconds(30);
    private const int MaxFolderDepth = 6;
    private const int MaxEntries = 20_000;
    private static readonly SemaphoreSlim ScanGate = new(1);

    // Folders that never hold a program or a game file a person would open.
    private static readonly string[] SkippedFolders =
    [
        "dev_hdd0", "dev_flash", "dev_usb000", "TROPDIR", "USRDIR", "Save Files", "cache", "caches", "shaderlog",
        "shadercache", "shaders", "System Volume Information", "$RECYCLE.BIN", ".git", "node_modules",
    ];

    /// <summary>One kind of emulator: the program names it installs under, the game files it opens, and what it is called.</summary>
    private sealed record EmulatorSpec(string Name, string Platform, string[] Programs, string[] Extensions);

    private static readonly EmulatorSpec[] Specs =
    [
        new("RPCS3", "PlayStation 3", ["rpcs3.exe"], [".iso"]),
        new("Dolphin", "GameCube and Wii", ["Dolphin.exe"], [".iso", ".gcm", ".wbfs", ".rvz", ".gcz", ".ciso", ".wad"]),
        new("DuckStation", "PlayStation", ["duckstation*.exe"], [".cue", ".chd", ".pbp"]),
        new("PCSX2", "PlayStation 2", ["pcsx2*.exe"], [".iso", ".chd", ".cso"]),
        new("Cemu", "Wii U", ["Cemu.exe"], [".wud", ".wux"]),
        new("Switch emulator", "Nintendo Switch", ["suyu.exe", "yuzu.exe", "eden.exe", "sudachi.exe", "citron.exe", "Ryujinx.exe"], [".nsp", ".xci"]),
        new("Xenia", "Xbox 360", ["xenia*.exe"], [".iso", ".xex"]),
        new("DeSmuME", "Nintendo DS", ["DeSmuME*.exe"], [".nds"]),
        new("fceux", "NES", ["fceux*.exe"], [".nes", ".fds"]),
        new("Project64", "Nintendo 64", ["Project64*.exe"], [".z64", ".n64", ".v64"]),
        new("PPSSPP", "PlayStation Portable", ["PPSSPP*.exe"], [".cso", ".pbp"]),
        new("mGBA", "Game Boy and Game Boy Advance", ["mGBA*.exe"], [".gba", ".gb", ".gbc"]),
    ];

    private static readonly HashSet<string> GameExtensions = new(Specs.SelectMany(spec => spec.Extensions), StringComparer.OrdinalIgnoreCase);

    /// <summary>An emulator program found on this machine. Its home is the folder that holds its games.</summary>
    private sealed record EmulatorInstance(EmulatorSpec Spec, string Program, string Home);

    /// <summary>What one chosen folder contained: emulator programs and game files.</summary>
    private sealed record FolderFindings(string Root, List<string> Programs, List<string> GameFiles);

    private static readonly Regex BracketedTags = new(@"\s*[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled);

    /// <summary>Scans on a background thread. Results come back as one list.</summary>
    public static async Task<IReadOnlyList<GameEntry>> ScanAsync(IReadOnlyList<AppRecord> installed, IReadOnlyList<string> folders)
    {
        if (!await ScanGate.WaitAsync(TimeSpan.FromSeconds(1)))
            throw new IOException("An earlier game scan is still waiting for storage. Try again later.");
        Task<IReadOnlyList<GameEntry>> task;
        try
        {
            task = Task.Factory.StartNew<IReadOnlyList<GameEntry>>(() => {
                try
                {
                    using var deadline = new CancellationTokenSource(FolderLimit);
                    return ScanAll(installed, folders.Take(32).ToArray(), deadline.Token);
                }
                finally { ScanGate.Release(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { ScanGate.Release(); throw; }
        return await task.WaitAsync(FolderLimit);
    }

    private static List<GameEntry> ScanAll(IReadOnlyList<AppRecord> installed, IReadOnlyList<string> folders, CancellationToken cancellation)
    {
        var games = new List<GameEntry>();
        games.AddRange(Safely(() => ScanSteam(cancellation)));
        games.AddRange(Safely(() => ScanEpic(cancellation)));

        // A folder on a network share can stall, so each walk gets a time limit.
        var findings = new List<FolderFindings>();
        int remaining = MaxEntries;
        foreach (string root in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            if (remaining <= 0) break;
            FolderFindings found = Walk(root, cancellation, remaining);
            findings.Add(found);
            remaining -= found.Programs.Count + found.GameFiles.Count;
        }

        List<EmulatorInstance> emulators = FindEmulators(installed, findings);
        games.AddRange(Safely(() => Rpcs3Titles(emulators, cancellation)));
        games.AddRange(Safely(() => GameFiles(findings, emulators)));

        // The same game can be reached twice, such as with two copies of one emulator. The first one found is kept.
        cancellation.ThrowIfCancellationRequested();
        return games.DistinctBy(game => game.Record.Id, StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToList();
    }

    /// <summary>Collects the games from one source. An unreadable library is skipped; the rest still show.</summary>
    private static List<GameEntry> Safely(Func<IEnumerable<GameEntry>> source)
    {
        try
        {
            return source().Take(MaxEntries).ToList();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return [];
        }
    }

    // ---- Steam -----------------------------------------------------------

    private static IEnumerable<GameEntry> ScanSteam(CancellationToken cancellation)
    {
        string? steam = SteamRoot();
        if (steam is null) yield break;

        // Games can live in several libraries, listed in libraryfolders.vdf.
        var libraries = new List<string> { steam };
        string libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile) && KeyValues.Parse(BoundedMetadata.ReadText(libraryFile)).GetValueOrDefault("libraryfolders") is Dictionary<string, object> folders)
        {
            foreach (object entry in folders.Values)
            {
                string? path = entry switch
                {
                    string direct => direct,
                    Dictionary<string, object> nested when nested.GetValueOrDefault("path") is string value => value,
                    _ => null,
                };
                if (path is not null) libraries.Add(path);
            }
        }

        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                cancellation.ThrowIfCancellationRequested();
                if (ReadSteamManifest(manifest, steamApps) is { } game) yield return game;
            }
        }
    }

    private static string? SteamRoot()
    {
        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string? path = user?.GetValue("SteamPath") as string;
        if (path is null || !Directory.Exists(path.Replace('/', '\\')))
        {
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            path = machine?.GetValue("InstallPath") as string;
        }
        path = path?.Replace('/', '\\');
        return path is not null && Directory.Exists(path) ? path : null;
    }

    private static GameEntry? ReadSteamManifest(string file, string steamApps)
    {
        try
        {
            if (KeyValues.Parse(BoundedMetadata.ReadText(file)).GetValueOrDefault("AppState") is not Dictionary<string, object> state) return null;
            string appId = Text(state, "appid");
            string name = Text(state, "name");
            if (appId.Length == 0 || name.Length == 0 || IsSteamTool(appId, name)) return null;

            return new GameEntry(new AppRecord
            {
                Id = "steam:" + appId,
                DisplayName = name,
                Publisher = "Steam",
                Technology = "steam",
                Scope = "currentUser",
                InstallLocation = Path.Combine(steamApps, "common", Text(state, "installdir")),
                InstallDate = UnixDate(Text(state, "LastUpdated")),
                LastRun = UnixInstant(Text(state, "LastPlayed")),
                EstimatedSizeBytes = UnsignedOrNull(Text(state, "SizeOnDisk")),
                Uninstall = new UninstallMethodInfo { Kind = "notAvailable", Reason = "Uninstall this game from Steam's library." },
            }, new LaunchTarget(LaunchKind.Uri, $"steam://rungameid/{appId}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Steam's own runtimes and shared components are listed as apps but are not games.</summary>
    private static bool IsSteamTool(string appId, string name) =>
        appId == "228980"
        || name.Contains("Proton", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

    // ---- Epic Games Store ------------------------------------------------

    private static IEnumerable<GameEntry> ScanEpic(CancellationToken cancellation)
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            cancellation.ThrowIfCancellationRequested();
            if (ReadEpicItem(file) is { } game) yield return game;
        }
    }

    private static GameEntry? ReadEpicItem(string file)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(BoundedMetadata.ReadText(file));
            JsonElement item = document.RootElement;
            string appName = Json(item, "AppName");
            string name = Json(item, "DisplayName");
            string location = Json(item, "InstallLocation");
            if (appName.Length == 0 || name.Length == 0 || Flag(item, "bIsApplication") == false || Flag(item, "bIsIncompleteInstall") == true)
            {
                return null;
            }

            string executable = Path.Combine(location, Json(item, "LaunchExecutable"));
            ulong? size = item.TryGetProperty("InstallSize", out JsonElement value) && value.TryGetUInt64(out ulong bytes) && bytes > 0 ? bytes : null;
            return new GameEntry(new AppRecord
            {
                Id = "epic:" + appName,
                DisplayName = name,
                Publisher = "Epic Games Store",
                Technology = "epic",
                Scope = "currentUser",
                InstallLocation = location,
                EstimatedSizeBytes = size,
                IconSource = File.Exists(executable) ? executable : null,
                Uninstall = new UninstallMethodInfo { Kind = "notAvailable", Reason = "Uninstall this game from the Epic Games Launcher." },
            }, new LaunchTarget(LaunchKind.Uri, $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ---- Emulators -------------------------------------------------------

    /// <summary>
    /// Walks each chosen folder for emulator programs and game files. The walk stops at a fixed depth and skips folders
    /// that hold saves, caches, and game data rather than things to open.
    /// </summary>
    private static FolderFindings Walk(string root, CancellationToken cancellation, int budget)
    {
        var programs = new List<string>();
        var games = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        int visited = 0;
        while (pending.Count > 0 && programs.Count + games.Count < budget && visited < 100_000)
        {
            cancellation.ThrowIfCancellationRequested();
            var (folder, depth) = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++visited > 100_000 || programs.Count + games.Count >= budget) break;
                    string extension = Path.GetExtension(file);
                    if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsEmulatorProgram(Path.GetFileName(file)) && !IsOldCopy(file)) programs.Add(file);
                    }
                    else if (GameExtensions.Contains(extension))
                    {
                        games.Add(file);
                    }
                }

                if (depth >= MaxFolderDepth) continue;
                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++visited > 100_000 || pending.Count >= 20_000) break;
                    bool skipped = SkippedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase);
                    bool link = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0;
                    if (!skipped && !link) pending.Push((child, depth + 1));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read is skipped.
            }
        }
        return new FolderFindings(root, programs, games);
    }

    /// <summary>Folders named like "rpcs3_old" hold superseded copies of an emulator. They are never used to open a game.</summary>
    private static bool IsOldCopy(string program) =>
        Path.GetDirectoryName(program)!.Split(Path.DirectorySeparatorChar).Any(part => part.Contains("_old", StringComparison.OrdinalIgnoreCase));

    private static bool IsEmulatorProgram(string fileName) =>
        AppLauncher.IsMainProgram(fileName)
        && Specs.Any(spec => spec.Programs.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true)));

    /// <summary>Emulators installed here: programs found in the chosen folders, and programs that installed apps point to.</summary>
    private static List<EmulatorInstance> FindEmulators(IReadOnlyList<AppRecord> installed, IReadOnlyList<FolderFindings> findings)
    {
        var instances = new List<EmulatorInstance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string program, string? root)
        {
            if (!seen.Add(program) || !IsEmulatorProgram(Path.GetFileName(program))) return;
            string fileName = Path.GetFileName(program);
            EmulatorSpec spec = Specs.First(candidate => candidate.Programs.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true)));
            string home = root is null ? Path.GetDirectoryName(program)! : HomeFolder(root, program);
            instances.Add(new EmulatorInstance(spec, program, home));
        }

        foreach (FolderFindings found in findings)
        {
            foreach (string program in found.Programs) Add(program, found.Root);
        }
        foreach (AppRecord app in installed)
        {
            string? program = IconLoader.ResolvePath(app);
            if (program is not null && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(program)) Add(program, null);
        }
        return instances;
    }

    /// <summary>The folder directly under the chosen root that holds the program. An emulator's games sit beside it there.</summary>
    private static string HomeFolder(string root, string program)
    {
        string[] parts = Path.GetRelativePath(root, program).Split(Path.DirectorySeparatorChar);
        return parts.Length > 1 ? Path.Combine(root, parts[0]) : root;
    }

    /// <summary>Installed PS3 games: each title folder in RPCS3's own dev_hdd0 that holds a main program.</summary>
    private static IEnumerable<GameEntry> Rpcs3Titles(IReadOnlyList<EmulatorInstance> emulators, CancellationToken cancellation)
    {
        foreach (EmulatorInstance emulator in emulators.Where(instance => instance.Spec.Name == "RPCS3"))
        {
            string games = Path.Combine(Path.GetDirectoryName(emulator.Program)!, "dev_hdd0", "game");
            if (!Directory.Exists(games)) continue;
            foreach (string title in Directory.EnumerateDirectories(games))
            {
                cancellation.ThrowIfCancellationRequested();
                if (ReadRpcs3Title(emulator, title) is { } game) yield return game;
            }
        }
    }

    private static GameEntry? ReadRpcs3Title(EmulatorInstance emulator, string title)
    {
        string id = Path.GetFileName(title);
        string eboot = Path.Combine(title, "USRDIR", "EBOOT.BIN");
        if (id.StartsWith('.') || id.StartsWith('＄') || id.EndsWith("_INSTALL", StringComparison.OrdinalIgnoreCase) || !File.Exists(eboot))
        {
            return null;
        }

        string icon = Path.Combine(title, "ICON0.PNG");
        return new GameEntry(new AppRecord
        {
            Id = "rpcs3:" + id,
            DisplayName = ParamSfo.ReadTitle(Path.Combine(title, "PARAM.SFO")) ?? id,
            Publisher = $"PlayStation 3 · {emulator.Spec.Name}",
            Technology = "emulator",
            Scope = "currentUser",
            InstallLocation = title,
            IconSource = File.Exists(icon) ? icon : null,
            Uninstall = new UninstallMethodInfo { Kind = "notAvailable", Reason = "Installed PS3 games are removed inside RPCS3. Deleting the title's folder under dev_hdd0 also removes it." },
        }, new LaunchTarget(LaunchKind.Program, emulator.Program, $"\"{eboot}\"", Path.GetDirectoryName(emulator.Program)));
    }

    /// <summary>Game files each attributed to one emulator: the one installed in the file's own folder, or the only one that opens that type.</summary>
    private static IEnumerable<GameEntry> GameFiles(IReadOnlyList<FolderFindings> findings, IReadOnlyList<EmulatorInstance> emulators)
    {
        foreach (FolderFindings found in findings)
        {
            foreach (string file in found.GameFiles)
            {
                if (OwnerOf(file, emulators) is { } owner && FileGame(owner, file) is { } game) yield return game;
            }
        }
    }

    private static EmulatorInstance? OwnerOf(string file, IReadOnlyList<EmulatorInstance> emulators)
    {
        string extension = Path.GetExtension(file);
        EmulatorInstance[] candidates = emulators.Where(instance => instance.Spec.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return null;

        string separator = Path.DirectorySeparatorChar.ToString();
        EmulatorInstance? home = candidates
            .Where(instance => file.StartsWith(instance.Home + separator, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.Home.Length)
            .FirstOrDefault();
        if (home is not null) return home;

        // Outside every emulator's folder, only a file type that a single kind of emulator opens is attributed.
        return candidates.Select(candidate => candidate.Spec.Name).Distinct().Count() == 1 ? candidates[0] : null;
    }

    private static GameEntry? FileGame(EmulatorInstance owner, string file)
    {
        try
        {
            return new GameEntry(new AppRecord
            {
                Id = "emu:" + file,
                DisplayName = CleanName(Path.GetFileNameWithoutExtension(file)),
                Publisher = $"{owner.Spec.Platform} · {owner.Spec.Name}",
                Technology = "emulator",
                Scope = "currentUser",
                InstallLocation = Path.GetDirectoryName(file),
                EstimatedSizeBytes = (ulong)new FileInfo(file).Length,
                IconSource = owner.Program,
                Uninstall = new UninstallMethodInfo { Kind = "notAvailable", Reason = "Game files are kept. Delete the file yourself, or use Open install location." },
            }, new LaunchTarget(LaunchKind.Program, owner.Program, ArgumentsFor(owner, file), Path.GetDirectoryName(owner.Program)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The command line each emulator family expects for opening one game file.</summary>
    private static string ArgumentsFor(EmulatorInstance owner, string file)
    {
        string quoted = $"\"{file}\"";
        string program = Path.GetFileName(owner.Program);
        return owner.Spec.Name switch
        {
            "Dolphin" => $"-e {quoted}",
            "Cemu" => $"-g {quoted}",
            "Switch emulator" when !program.Equals("Ryujinx.exe", StringComparison.OrdinalIgnoreCase) => $"-g {quoted}",
            // Qt builds take the file after a "--" separator.
            "DuckStation" or "PCSX2" when program.Contains("qt", StringComparison.OrdinalIgnoreCase) => $"-- {quoted}",
            _ => quoted,
        };
    }

    /// <summary>A readable name from a file name: the bracketed tags such as "(USA)" or "[SLUS-00001]" are dropped.</summary>
    private static string CleanName(string fileName)
    {
        string cleaned = BracketedTags.Replace(fileName, "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Length > 0 ? cleaned : fileName;
    }

    // ---- Small helpers -----------------------------------------------------

    private static string Text(Dictionary<string, object> values, string key) => values.GetValueOrDefault(key) as string ?? "";

    private static string? UnixDate(string seconds) =>
        UnixInstant(seconds)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? UnixInstant(string seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
            : null;

    private static ulong? UnsignedOrNull(string text) =>
        ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value) && value > 0 ? value : null;

    private static string Json(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool? Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
