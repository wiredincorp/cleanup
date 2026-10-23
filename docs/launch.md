# Download and updates

## Windows downloads

The main download is **`Cleanup.exe`**. Download it and double-click it. No
installer, administrator account, or separate .NET / Windows App SDK install is
needed for normal use. On first launch, the bundled WinUI files and Rust core
extract to `%TEMP%\.net\Cleanup`; Windows reuses them on later launches. Older
cache folders are removed in the background when Windows lets Cleanup do so.

The linked `v0.1.2-rc.1` preview stores settings and history in
`%LOCALAPPDATA%\Cleanup`. USB data portability is being added to the next
portable build: it will store settings and history in `CleanupData` beside the
EXE when writable, and fall back to `%LOCALAPPDATA%\Cleanup` when that location
is read-only. Keep `Cleanup.exe` and `CleanupData` together when moving a
portable copy between PCs. The current published preview does not yet carry its
data on a USB drive.

For a Start menu shortcut and an uninstaller, use the per-user **setup EXE**.
Setup stores data in `%LOCALAPPDATA%\Cleanup`; uninstalling keeps that data.

The verified target is **Windows 11 x64**. Both downloads include .NET,
Windows App SDK / WinUI, and the Rust core. They do not need a separately
installed Visual C++ redistributable. The portable EXE still extracts native
support files to the host's temp cache on first launch; it is one download file,
not a zero-extraction process. ARM64 emulation is unverified.

The preview is unsigned, so Windows may show an unknown-publisher warning.
Cleanup normally runs as a standard user; an application's own uninstaller may
ask Windows for approval.

## Updates and source

Use **Settings → Check for updates**, then replace `Cleanup.exe` or run the
newer setup EXE. Keep `CleanupData` beside the EXE when using a portable build
with USB data portability.

The project is open source. See the [development guide](development.md) for
source builds and developer validation.
