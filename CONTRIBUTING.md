# Contributing

cleanup is a native Windows 11 uninstaller and folder organizer. Changes should
keep the interface small and the filesystem behavior explicit. The source is
licensed under [MIT](LICENSE); dependencies retain their own licenses.

## Development

Use Windows 11 x64, PowerShell 7, the .NET SDK in `global.json`, the Rust toolchain
in `rust-toolchain.toml`, Visual Studio C++ Build Tools and Windows SDK 26100.
See the [development guide](docs/development.md) for certificate trust and installation instructions.

```powershell
.\scripts\verify-core.ps1
.\scripts\package-dev.ps1
.\scripts\install-dev.ps1 -Mode Msix
```

`-UseLocalFeed` is a documented local restore fallback. Commit the NuGet lock file
and Cargo.lock when dependencies change. CI restores in locked mode and builds a
development-signed x64 MSIX; that artifact is not a public release.

## Changes and verification

- Keep C# presentation separate from Rust system operations and the versioned ABI.
- Preserve official uninstall mechanisms, explicit confirmation, no-overwrite
  behavior, protected paths, reparse checks and identity checks during undo.
- Cleanup starts as a standard user. Elevation stays opt-in, goes through a Windows
  approval prompt, and follows [ADR 0004](docs/adr/0004-opt-in-elevation.md). Do not
  make it the default, skip the prompt, or rely on it in place of the checks above.
- Verify destructive workflows only with namespaced fixtures in disposable
  directories or virtual machines. Never use personal files or installed apps.
- Include reproduction steps, build/test commands and their actual outcomes.
  Describe any untested packaged behavior and accessibility or performance impact.
- Include screenshots for UI changes. Honor Windows animation and contrast settings.
- Do not add optimizers, debloating, automatic cleanup, services or network backends.

## Reports

Use a small fixture and sanitized paths when reporting a bug. Inventory and journal
files can include private application names and full user paths; do not attach them
without reviewing them. See [SECURITY](SECURITY.md) for sensitive reports.
