# Full-trust MSIX with separate development signing

Package Cleanup as a WinUI 3 desktop application with `runFullTrust` and an `asInvoker` executable manifest. Ship the self-contained .NET 10 runtime inside the package and declare the Windows App Runtime framework dependency, accepting the larger package to avoid requiring a separately installed .NET runtime. The Rust DLL ships beside the executable; state goes to package LocalState.

Development uses `Cleanup.Dev`, publisher `CN=Cleanup Dev`, and a nonexportable private key in the current user's certificate store. Trust only its public certificate in machine TrustedPeople through explicit UAC-approved development setup. A loose Developer Mode registration provides useful package-identity evidence but does not count as signed installation into WindowsApps. Release identity and signing credentials must be configured separately before any distribution; no release signing or Store certification is claimed.

Official Win32 uninstallers launch from their registered, validated executable path. MSI uses the documented system `msiexec /x` interface with passive progress and reboot suppression in this feasibility build; direct MSI configuration APIs remain a Phase 3 evaluation. MSIX uses PackageManager. ~~Only an official uninstaller may request elevation through ShellExecute `runas`; Cleanup stays unelevated, with no privileged service or helper.~~

**Superseded in part (2026-10-29):** the struck sentence is replaced by [ADR 0004](0004-opt-in-elevation.md). Cleanup still starts unelevated with an `asInvoker` manifest and no privileged service. Official uninstallers still request their own elevation, a one-shot helper reads the administrator activity log after a Windows prompt, and an elevated main window is opt-in.
