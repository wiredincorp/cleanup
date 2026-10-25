<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="website/public/logo-dark.svg">
    <img src="website/public/logo.svg" alt="" width="64" height="64">
  </picture>
  <h1>cleanup</h1>
  <p>A little less clutter. A lot more room.</p>
  <p>A native Windows 11 app for removing applications and organizing folders.</p>
  <p>
    <a href="https://cleanup-wiredin.vercel.app">Website</a> ·
    <a href="https://github.com/wiredincorp/cleanup/releases">Releases</a> ·
    <a href="https://discord.gg/5kRtCcNWU6">Discord</a>
  </p>
</div>

## A place for everything

- **See your space.** View used and free storage across your drives.
- **Let apps go.** Find applications, review your selection, and run their official uninstallers. Review leftovers separately.
- **Sort the pile.** Preview folder organization, choose what moves, and save rules for next time.
- **Keep a clear history.** Review results and undo eligible file moves.

Made with WinUI 3 and Rust, with light and dark themes that feel at home on Windows.

## Download for Windows

**[Download Cleanup.exe](https://github.com/wiredincorp/cleanup/releases/download/v0.1.6-rc.1/Cleanup.exe)** and double-click it. It runs as a standard user and includes its runtime. On first launch, Windows support files are extracted to a temporary folder.

For a Start menu shortcut and an uninstaller, use the [setup EXE](https://github.com/wiredincorp/cleanup/releases/download/v0.1.6-rc.1/cleanup-0.1.6-windows-x64-setup.exe). Some applications' official uninstallers may ask Windows for approval.

Windows 11 x64. For USB portability, keep `Cleanup.exe` and its `CleanupData` folder together to carry settings and history between PCs. If that location is read-only, Cleanup uses your Windows profile instead. This unsigned preview may show an unknown-publisher warning.

To update, use Settings > Check for updates, then replace `Cleanup.exe` and keep `CleanupData` beside it. ARM64 emulation is unverified; macOS/Linux builds are unavailable.

Open-source source and developer build notes are in the [development guide](docs/development.md).

## Added

- **Recently running** sort. The same apps stay in the list. Programs that are open say **Running**. The others show the date they last ran.
- While that sort is selected, the date column is labeled **Last run**. Other sorts still show the install date.
- Last run comes from Windows’ own records: the normal account history, the administrator activity log, Store app last-use data (this fills in Xbox), and Steam’s **Last played** time. Epic games match when that game’s program was actually started.
- Windows asks once to read the administrator log. The result is kept for 12 hours, so it does not ask on every launch.
- **Size** for programs whose installer never recorded one. Cleanup adds up that app’s own folder after the list appears. The total at the top includes those sizes.
- Press **Size** while **Recently running** is selected and the list stays on recently run apps. First press is largest first. Second press is smallest. **Last run** stays visible. Apps with no size stay at the bottom.
- More installed programs are listed, including items marked as system components, such as NVIDIA pieces and Visual C++ runtimes, and installs that have no uninstall command. **Remove** stays off when there is no uninstaller.

## Fixed

- Install folders saved with a trailing slash now match the program inside them.
- Apps whose registered program is `uninstall.exe`, such as Steam and MSI Afterburner, now match the program you actually open.
- Store apps match even when Windows puts a version number in the folder name. That was why Xbox looked like it had never been run.
- A program is also matched when its file or folder name is the app’s name, including Brave and Edge.
- Shortcut targets are followed, so a last-run record that points at a shortcut still counts.
- Setup programs, uninstallers, and other generic helper names do not stamp the wrong last-run date.
- Folder sizes do not follow links or junctions, and they do not total shared locations such as Program Files, Downloads, or your profile.
- A folder that takes too long still keeps the size counted so far.
- Emulator games are not given the emulator’s last-run time.
- Windows updates, patches, Microsoft system pieces with no uninstaller, Microsoft system pieces in the Windows folder, Edge WebView2, the application compatibility database, the servicing stack, and built-in Windows packages stay off the list. Visual C++, the .NET runtimes, and the Windows SDK stay.

## Around the project

[Contributing](CONTRIBUTING.md) · [Launch and updates](docs/launch.md) · [Security](SECURITY.md)

Licensed under [MIT](LICENSE). [Third-party notices](THIRD_PARTY_NOTICES.md).

©(wired-in) community
