# Developer guide

Cleanup is an open-source Windows 11 x64 application. The repository includes
the WinUI frontend, Rust core, and scripts for creating the portable download
and optional per-user setup.

## Build and verify

Use Windows 11 x64 with the .NET 10 SDK, the Rust toolchain pinned in
`rust-toolchain.toml`, Visual Studio C++ Build Tools, Windows SDK
10.0.26100.0, and PowerShell 7. From the repository root:

```powershell
. .\scripts\env.ps1
.\scripts\verify-core.ps1
.\scripts\package-desktop.ps1 -Version 0.1.2
.\scripts\verify-portable.ps1 -Version 0.1.2
.\scripts\verify-portable-locations.ps1 -Version 0.1.2
```

The package script creates a single-file `Cleanup.exe` and a per-user Setup
EXE. Both include .NET and Windows App SDK / WinUI and do not need a separate
Visual C++ redistributable. The portable EXE extracts native support files to
the user's temporary cache on first launch. Its settings and history go in
`CleanupData` beside the EXE when writable, with a per-user profile fallback
when the portable folder is read-only. Setup stores settings and history in
`%LOCALAPPDATA%\Cleanup`.

Run the location check from a standard, non-elevated account. It checks paths
with spaces, data migration, write-denied fallback, and a removable drive when
one is attached.

See [download and updates](launch.md) for the end-user behavior. Internal
development tooling is kept in `scripts/` and `packaging/`.
