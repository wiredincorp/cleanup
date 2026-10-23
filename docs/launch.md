# Download and updates

## Windows downloads

**[Download Cleanup.exe](https://github.com/wiredincorp/cleanup/releases/download/v0.1.4-rc.1/Cleanup.exe)** and double-click it. It runs as a standard user and includes its runtime. The supported target is Windows 11 x64.

For a Start menu shortcut and uninstaller, use the [per-user setup EXE](https://github.com/wiredincorp/cleanup/releases/download/v0.1.4-rc.1/cleanup-0.1.4-windows-x64-setup.exe).

## Portable use

For USB use, keep `Cleanup.exe` and the `CleanupData` folder together. Cleanup stores settings and history there when writable; if the drive is read-only, it falls back to your Windows profile.

On first launch, Windows support files are extracted to a temporary folder. Setup keeps data in `%LOCALAPPDATA%\Cleanup`; uninstalling Setup keeps that data. Some official uninstallers may ask Windows for approval.

This unsigned preview may show an unknown-publisher warning. A work or school policy may block it. ARM64 emulation is unverified.

## Updates and source

Use Settings > Check for updates, then replace `Cleanup.exe` and keep `CleanupData` beside it. The project is open source; the [developer guide](development.md) has build information.
