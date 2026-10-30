# Opt-in elevation for the main window

Status: accepted, 2026-10-29. Supersedes the elevation sentence of
[ADR 0002](0002-msix-packaging-and-signing.md): "Only an official uninstaller may
request elevation through ShellExecute `runas`; Cleanup stays unelevated, with no
privileged service or helper."

## Context

ADR 0002 kept Cleanup unelevated and left elevation to official uninstallers.
Contributor commit `a9c3857` (2026-10-25) added two features that read data a
standard token cannot:

- **Last run dates.** Windows keeps most execution history in a log that only an
  administrator can read. When the Recently running sort is chosen,
  `RecentActivity.RequestBackgroundLog` starts a short-lived copy of the same
  executable with `runas` and `--export-run-history <file>`. That copy reads the log,
  writes dates and paths to `cleanup-run-history.txt` in the user's temporary folder,
  and exits; it refuses any destination outside that folder. The file is reused for
  12 hours, and a declined prompt is not repeated in the same session. If elevation is
  declined or unavailable, last run dates come only from the standard account's history.
- **An elevated main window.** "Run as administrator" in the title bar explains what
  elevation adds and restarts Cleanup elevated (`App.RestartElevated`). Its "Always run
  this app as administrator" checkbox, and the "Always run as administrator" switch in
  Settings, store `RunAsAdministrator`. At launch, `App.TryStartElevated` then asks
  Windows to start an elevated copy.

Elevation lets more install folders be measured, reads the administrator activity log
directly, and lets uninstallers inherit the token so they do not prompt again.

## Decision

Cleanup starts as a standard user. The executable manifest stays `asInvoker`, and every
elevation is a run-time request that goes through Windows User Account Control.
Elevating the main window is opt-in and stays within these limits:

- **Off by default.** `RunAsAdministrator` is false in new settings. Only the user turns
  it on, with the checkbox in the Run as administrator dialog or the Settings switch.
  Self-test and normal-window smoke runs never elevate.
- **Windows asks on every launch.** With the setting on, each launch requests an
  elevated copy with ShellExecute `runas` and `--elevate-restart`, and closes only after
  that copy has started. There is no scheduled task, service, helper, or manifest change
  that skips the prompt. If the prompt is declined or elevation fails, the same launch
  continues unelevated.
- **It can be turned off.** The Settings switch takes effect at the next launch; the
  current window keeps its token. Declining the prompt always opens an unelevated
  window, where the switch can be turned off.
- **One restart, no loop.** `--elevate-restart` skips the setting check and waits up to
  15 seconds for the previous window to release the single-instance lock.
- **No separate privileged code path.** The elevated window runs the same frontend and
  Rust core. Previews, confirmations, organizer path guards (Windows, program,
  ProgramData and app data folders are refused), reparse and identity checks,
  one-use leftover approvals, and journaled undo apply unchanged.

The run-history helper stays a one-shot reader: it writes one file to the temporary
folder and exits. When Cleanup is unelevated, official uninstallers still request their
own elevation as described in ADR 0002.

## Consequences

- **Windows permissions stop protecting some locations.** Elevated, the organizer can
  move files in folders a standard user cannot change, such as another account's
  profile folders, and leftover removal can delete reviewed machine-wide items under
  ProgramData, Program Files, the shared Start menu, and HKLM `SOFTWARE`. Path guards,
  previews, one-use approvals, and second confirmations remain the protection.
- **Uninstallers inherit the token.** An uninstaller started from an elevated window
  runs elevated without its own prompt.
- **No drag and drop from Explorer.** Windows blocks dragging from an unelevated
  Explorer window into an elevated one, so "drop a folder here" on Organize, Photos,
  and Videos does not work while elevated. Choose folder does.
- **Different account, different Setup data.** If a standard user approves the prompt
  with an administrator's credentials, the elevated window runs as that administrator.
  A Setup install then reads and writes that account's `%LOCALAPPDATA%\Cleanup`, so its
  settings and history differ, and turning the switch off there does not change the
  standard user's setting. Declining the prompt and turning the switch off in the
  unelevated window does. The portable `CleanupData` folder beside `Cleanup.exe` is
  shared. The run-history helper writes nothing in this case, because the requested
  file is outside that account's temporary folder.
- **Verification is manual.** The elevated restart, the declined prompt, and turning the
  setting off need a person at the UAC prompt and are not covered by automated checks.
  `verify-portable.ps1` and `verify-ui-smoke.ps1` run without the setting; hosted CI
  runners are already elevated (`-AllowElevated`).

Making elevation the default, skipping the prompt, or adding a privileged service or
helper would need a new decision.
