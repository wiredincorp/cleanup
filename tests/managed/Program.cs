using Cleanup.Services;
using System.Text.Json;

int passed = 0;
void Check(string name, Action test) { test(); Console.WriteLine($"PASS {name}"); passed++; }
void Expect<T>(Action test) where T : Exception { try { test(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
ReleaseVersion V(string value) => ReleaseVersion.Parse(value) ?? throw new Exception(value);

Check("Steam metadata stays compatible", () => Assert(KeyValues.Parse("\"LibraryFolders\" { \"path\" \"D:\\\\games\" }")["libraryfolders"] is Dictionary<string, object>));
Check("deep metadata raises a recoverable exception", () => Expect<FormatException>(() => KeyValues.Parse(string.Concat(Enumerable.Repeat("a {", 50_000)))));
Check("oversized and unterminated metadata is rejected", () => { Expect<FormatException>(() => KeyValues.Parse(new string('a', 1024 * 1024 + 1))); Expect<FormatException>(() => KeyValues.Parse("\"unfinished")); });
Check("semantic release ordering", () => {
    string[] versions = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
    for (int i = 1; i < versions.Length; i++) Assert(V(versions[i-1]).CompareTo(V(versions[i])) < 0);
    Assert(V("v0.1.5-rc.1").CompareTo(V("v0.1.5-rc.2")) < 0);
    Assert(V("1.2.3+build").CompareTo(V("1.2.3")) == 0);
    Assert(ReleaseVersion.Parse("1.0.0-rc.01") is null);
});
Check("updates respect stable and preview channels", () => {
    using JsonDocument releases = JsonDocument.Parse("""[{"draft":false,"prerelease":true,"tag_name":"v0.1.5-rc.2","html_url":"https://github.com/test/app/releases/tag/v0.1.5-rc.2"},{"draft":false,"prerelease":false,"tag_name":"v0.1.4","html_url":"https://github.com/test/app/releases/tag/v0.1.4"}]""");
    var repo = new Uri("https://github.com/test/app");
    Assert(UpdateCheck.SelectNewer(releases.RootElement, repo, V("0.1.5-rc.1"))?.Tag == "v0.1.5-rc.2");
    Assert(UpdateCheck.SelectNewer(releases.RootElement, repo, V("0.1.3"))?.Tag == "v0.1.4");
    Assert(UpdateCheck.SelectNewer(releases.RootElement, repo, V("0.1.4")) is null);
});
Check("timed out storage work is reused and capped", () => {
    var flights = new SingleFlight<string, int>(1);
    using var release = new ManualResetEventSlim();
    Task<int> first = flights.Run("drive", () => { release.Wait(); return 7; });
    try {
        for (int i = 0; i < 100; i++) Assert(ReferenceEquals(first, flights.Run("drive", () => 9)));
        Expect<IOException>(() => flights.Run("another", () => 9).GetAwaiter().GetResult());
    } finally { release.Set(); first.GetAwaiter().GetResult(); }
    Assert(flights.Run("another", () => 9).GetAwaiter().GetResult() == 9);
});
Check("metadata read limits apply before allocation", () => {
    string root = Path.Combine(AppContext.BaseDirectory, "fixtures"); Directory.CreateDirectory(root);
    string file = Path.Combine(root, "large.dat");
    try { using (var stream = File.Create(file)) stream.SetLength(1024 * 1024 + 1); Expect<IOException>(() => BoundedMetadata.ReadBytes(file)); }
    finally { File.Delete(file); }
});
Check("execution history decodes a program path and its last run", () => {
    Assert(RecentActivity.Rot13(@"P:\Ncc\ncc.rkr") == @"C:\App\app.exe");
    var data = new byte[72];
    DateTime ran = new(2026, 10, 20, 15, 4, 0, DateTimeKind.Local);
    BitConverter.GetBytes(ran.ToFileTime()).CopyTo(data, 60);
    Assert(RecentActivity.LastExecuted(data) == ran);
    Assert(RecentActivity.LastExecuted(new byte[16]) is null);
    string? system = RecentActivity.ToExecutablePath(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe");
    Assert(system is not null && system.EndsWith(@"\notepad.exe", StringComparison.OrdinalIgnoreCase));
    Assert(RecentActivity.ToExecutablePath("UEME_CTLSESSION") is null);
});
Check("running and recent programs match their own folder only", () => {
    string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    string mine = Path.Combine(programs, "Mine");
    string mineExe = Path.Combine(mine, "mine.exe");
    var last = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { [mineExe] = DateTime.Now.AddHours(-3) };
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    RecentActivity.RunHit broad = RecentActivity.Match([Path.Combine(programs, "Other", "other.exe")], last, names, programs, mineExe);
    Assert(!broad.Running && broad.LastRan == last[mineExe]);
    Assert(RecentActivity.Match([Path.Combine(programs, "Other", "other.exe")], last, names, programs, null).LastRan is null);
    RecentActivity.RunHit hit = RecentActivity.Match([mineExe], last, names, mine, Path.Combine(programs, "Emulator", "emu.exe"));
    Assert(hit.Running && hit.LastRan == last[mineExe]);
    Assert(RecentActivity.Caption(true, null, DateTime.Now) == "Running");
    Assert(RecentActivity.Caption(false, DateTime.Now.AddMinutes(-5), DateTime.Now) == "Ran 5 min ago");
    Assert(RecentActivity.Caption(false, DateTime.Now.AddDays(-40), DateTime.Now) is null);
    var named = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["Brave"] = DateTime.Now.AddDays(-2), [@"E:\Other\mine.exe"] = DateTime.Now.AddDays(-1) };
    Assert(RecentActivity.Match([], named, new HashSet<string>(), null, @"D:\Apps\brave.exe", null, "Brave Browser").LastRan == named["Brave"]);
    Assert(RecentActivity.Match([], named, new HashSet<string>(), null, @"D:\Mine\mine.exe").LastRan == named[@"E:\Other\mine.exe"]);
    Assert(RecentActivity.Match([], new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { [@"E:\Other\setup.exe"] = DateTime.Now }, new HashSet<string>(), null, @"D:\Mine\setup.exe").LastRan is null);
    var blob = new byte[16];
    BitConverter.GetBytes(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Local).ToFileTime()).CopyTo(blob, 0);
    Assert(RecentActivity.ExecutionTime(blob)?.Date == new DateTime(2026, 10, 20));
    Assert(RecentActivity.ToDosPath(@"\Device\HarddiskVolume4\Apps\app.exe", new Dictionary<string, string> { [@"\Device\HarddiskVolume4"] = @"C:\" }) == @"C:\Apps\app.exe");
    var used = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Program Files\7-Zip\7zFM.exe"] = DateTime.Now.AddDays(-1),
        [@"C:\Program Files (x86)\Steam\Steam.exe"] = DateTime.Now.AddDays(-2),
    };
    Assert(RecentActivity.Match([], used, new HashSet<string>(), @"C:\Program Files\7-Zip\", @"C:\Program Files\7-Zip\7zFM.exe", null, "7-Zip 26.04 (x64)").LastRan == used[@"C:\Program Files\7-Zip\7zFM.exe"]);
    Assert(RecentActivity.Match([], used, new HashSet<string>(), null, @"C:\Program Files (x86)\Steam\uninstall.exe", null, "Steam").LastRan == used[@"C:\Program Files (x86)\Steam\Steam.exe"]);
    Assert(RecentActivity.PackageMatches(@"C:\Program Files\WindowsApps\Microsoft.GamingApp_2610.1001.20.0_x64__8wekyb3d8bbwe\XboxPcApp.exe", "Microsoft.GamingApp_8wekyb3d8bbwe"));
});
Check("install folder size skips shared roots and sums a private folder", () => {
    Assert(InstallSize.Measure(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), null, CancellationToken.None) is null);
    string root = Path.Combine(Path.GetTempPath(), "cleanup-size-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "nested"));
    File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[3]);
    File.WriteAllBytes(Path.Combine(root, "nested", "b.bin"), new byte[5]);
    try { Assert(InstallSize.Measure(root, null, CancellationToken.None) == 8); }
    finally { Directory.Delete(root, recursive: true); }
});
Check("folder scan lists biggest folders and keeps the chosen sort while searching", () => {
    string root = Path.Combine(Path.GetTempPath(), "cleanup-folders-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "big"));
    Directory.CreateDirectory(Path.Combine(root, "small"));
    File.WriteAllBytes(Path.Combine(root, "big", "a.bin"), new byte[50]);
    File.WriteAllBytes(Path.Combine(root, "small", "b.bin"), new byte[10]);
    File.WriteAllBytes(Path.Combine(root, "big", "needle.txt"), new byte[4]);
    try
    {
        FolderScan.Result scan = FolderScan.Read(root, CancellationToken.None);
        Assert(scan.Root.Size == 64);
        Assert(scan.Log.Any(line => line.Contains("files", StringComparison.OrdinalIgnoreCase)));
        IReadOnlyList<FolderScan.Node> biggest = FolderScan.Rows(scan.Root, null, FolderScan.Column.Size, descending: true);
        Assert(biggest[0].Name == "big" && biggest[0].Size == 54);
        Assert(biggest[1].Name == "small" && biggest[1].Size == 10);
        IReadOnlyList<FolderScan.Node> smallest = FolderScan.Rows(scan.Root, null, FolderScan.Column.Size, descending: false);
        Assert(smallest[0].Name == "small" && smallest[1].Name == "big");
        IReadOnlyList<FolderScan.Node> byName = FolderScan.Rows(scan.Root, null, FolderScan.Column.Name, descending: false);
        Assert(byName[0].Name == "big" && byName[1].Name == "small");
        IReadOnlyList<FolderScan.Node> found = FolderScan.Rows(scan.Root, "NEEDLE", FolderScan.Column.Name, descending: false);
        Assert(found.Count == 1 && found[0].Name == "needle.txt" && found[0].Size == 4 && !found[0].IsFolder);
        Assert(FolderScan.Rows(biggest[0], "b.bin", FolderScan.Column.Size, descending: true).Count == 0);
        FolderScan.Result missing = FolderScan.Read(Path.Combine(root, "missing"), CancellationToken.None);
        Assert(missing.Skipped == 1);
        Assert(missing.Log.Any(line => line.Contains("missing", StringComparison.OrdinalIgnoreCase)));
    }
    finally { Directory.Delete(root, recursive: true); }
});
Check("a folder shows as soon as it is measured and size sort keeps the largest on top", () => {
    string root = Path.Combine(Path.GetTempPath(), "cleanup-folders-live-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "big"));
    Directory.CreateDirectory(Path.Combine(root, "small"));
    File.WriteAllBytes(Path.Combine(root, "big", "a.bin"), new byte[50]);
    File.WriteAllBytes(Path.Combine(root, "small", "b.bin"), new byte[10]);
    var shown = new FolderScan.Node { Name = "device", IsFolder = true };
    try
    {
        int updates = 0;
        FolderScan.Result scan = FolderScan.ReadStreaming(root, CancellationToken.None, child =>
        {
            Assert(child.IsFolder);
            Assert(child.Name == "big" ? child.Size == 50 : child.Name == "small" && child.Size == 10);
            shown.Children.Add(child);
            updates++;
            IReadOnlyList<FolderScan.Node> rows = FolderScan.Rows(shown, null, FolderScan.Column.Size, descending: true);
            Assert(rows[0].Size == rows.Max(node => node.Size));
            if (rows.Count == 2)
            {
                Assert(rows[0].Name == "big" && rows[1].Name == "small");
            }
        });
        Assert(updates == 2);
        Assert(scan.Root.Size == 60);
    }
    finally { Directory.Delete(root, recursive: true); }
});
Check("each folder is reported when its own size is finished", () => {
    string root = Path.Combine(Path.GetTempPath(), "cleanup-folders-one-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "big", "nested"));
    Directory.CreateDirectory(Path.Combine(root, "small"));
    File.WriteAllBytes(Path.Combine(root, "big", "a.bin"), new byte[50]);
    File.WriteAllBytes(Path.Combine(root, "big", "nested", "c.bin"), new byte[7]);
    File.WriteAllBytes(Path.Combine(root, "small", "b.bin"), new byte[10]);
    var seen = new List<FolderScan.Node>();
    try
    {
        FolderScan.Result scan = FolderScan.Read(root, CancellationToken.None, onFolder: seen.Add);
        int nested = seen.FindIndex(node => node.Name == "nested");
        int big = seen.FindIndex(node => node.Name == "big");
        int small = seen.FindIndex(node => node.Name == "small");
        Assert(nested >= 0 && big > nested && small >= 0);
        Assert(seen[nested].Size == 7);
        Assert(seen[big].Size == 57);
        Assert(seen[small].Size == 10);
        Assert(scan.Root.Size == 67);
    }
    finally { Directory.Delete(root, recursive: true); }
});
Check("folder scan leaves out folders Windows needs to run", () => {
    Assert(FolderScan.IsProtectedSystem(@"C:\Windows"));
    Assert(FolderScan.IsProtectedSystem(@"C:\Windows\System32\ntoskrnl.exe"));
    Assert(FolderScan.IsProtectedSystem(@"C:\System Volume Information\tracking.log"));
    Assert(FolderScan.IsProtectedSystem(@"C:\$Recycle.Bin\S-1-5-21"));
    Assert(FolderScan.IsProtectedSystem(@"C:\Recovery"));
    Assert(FolderScan.IsProtectedSystem(@"C:\pagefile.sys"));
    Assert(FolderScan.IsProtectedSystem(@"C:\hiberfil.sys"));
    Assert(FolderScan.IsProtectedSystem(@"C:\swapfile.sys"));
    Assert(FolderScan.IsProtectedSystem(@"C:\Program Files\WindowsApps\Microsoft.Windows.Shell"));
    Assert(FolderScan.IsProtectedSystem(@"C:\Program Files\Windows Defender\MsMpEng.exe"));
    Assert(FolderScan.IsProtectedSystem(@"C:\Program Files (x86)\Windows NT\Accessories"));
    Assert(!FolderScan.IsProtectedSystem(@"C:\Users\me\Documents"));
    Assert(!FolderScan.IsProtectedSystem(@"C:\Program Files\Steam"));
    Assert(!FolderScan.IsProtectedSystem(@"D:\Games\Windows"));
});
Console.WriteLine($"{passed} managed regression checks passed.");
