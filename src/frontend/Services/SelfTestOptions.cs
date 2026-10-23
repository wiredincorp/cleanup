namespace Cleanup.Services;

/// <summary>
/// Command-line options for the Phase 0 feasibility run, e.g.
/// <c>cleanup-dev.exe --selftest --report C:\out\report.json --fixture-dir C:\out\moves --uninstall-fixtures</c>.
/// </summary>
internal sealed record SelfTestOptions(
    bool Enabled,
    string? ReportPath,
    string? FixtureDirectory,
    string? LocalAppDataRoot,
    bool UninstallFixtures,
    bool AllowElevation)
{
    public static SelfTestOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        return new SelfTestOptions(
            Enabled: Flag("--selftest"),
            ReportPath: Value("--report"),
            FixtureDirectory: Value("--fixture-dir"),
            LocalAppDataRoot: Value("--local-app-data-root"),
            UninstallFixtures: Flag("--uninstall-fixtures"),
            AllowElevation: Flag("--allow-elevation"));
    }
}
