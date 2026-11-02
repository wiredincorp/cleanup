# Release hardening, 2026-10-23

The release audit's ten concrete code findings have remediation in this checkout.
This is a source and local verification result, not certification of a signed public release.
The native boundary is now ABI 3; ship the matching frontend and native DLL together.

## Changes

| Finding | Remediation |
| --- | --- |
| Source replacement between identity check and move/undo | Own a handle with delete access that denies concurrent writes/deletes; verify and rename through that handle. Cross-volume copies use Windows backup streams, preserve named data streams and dates, flush the destination, then delete through the owned source handle. Unsupported streams/encrypted files fail with the source preserved. |
| Search-path execution of `explorer.exe` | Use the executable under the Windows directory and pass the folder as one argument. |
| Stale leftover approval deleting replacement/reinstalled data | Return expiring, one-use IDs bound to the reviewed app context and an inspected filesystem/registry snapshot. Revalidate snapshots and refresh installed applications before removal; refuse inventory failures or a reappeared app. Bound approval memory to 16 MiB. |
| KeyValues recursion exhausting the process stack | Limit metadata to 1 MiB and nesting to 64 levels; reject malformed quoted strings with a recoverable exception. Bound SFO reads too. |
| Timed-out synchronous probes accumulating workers | Reuse outstanding drive probes with a worker cap; allow only one actual game scan at a time, including after timeout. Add cooperative deadlines, traversal budgets, result limits and bounded metadata reads. |
| Crash between moving a file and journaling completion | Flush `MovePrepared` / `UndoPrepared` with the expected destination identity before committing. Recover interrupted states only when the source/destination state and identity match. |
| Preview accepted but apply exceeded the 4 MiB ABI limit | Raise the bounded request limit to 16 MiB and reject previews whose managed serialization could exceed it. Include escaping overhead. Remove quadratic approval lookup during application. |
| Preview/stable updates compared as equal | Compare semantic prerelease identifiers, retain the full informational version in packaging, and keep stable builds on the stable channel. Validate release-page origins and bound downloaded metadata. |
| Whole-lifetime history in memory | Write new events into per-batch logs, stream existing legacy history unchanged, load 20 batches per page, and read only the selected batch for undo. Bound event, batch and page bytes. Search and filters apply to the displayed page. |
| Uninstall waiting could depend on a missed job notification | Query active job processes while polling. Detach safely on observation failure or cancellation; never terminate the uninstaller. |

Animation tweens now detach their timer callbacks when stopped or completed. The normal-window
smoke run records managed memory, private bytes, handles and threads after diagnostic collection.
Collection is confined to this explicit diagnostic mode; ordinary application behavior does not force GC.

## Release tooling

- `verify-managed.ps1` runs dependency-free .NET regression checks against actual service sources.
- `verify-ui-smoke.ps1 -ExecutablePath <exe>` opens the actual MainWindow, cycles seven pages
  (including Photos and Videos) ten times and exercises production organizer and photo-sorting
  preview/apply/undo with unique disposable files.
  It requires isolated diagnostic paths, and normal singleton/cache initialization remains active.
- The feasibility test also exercises the production organizer; probe moves remain separate diagnostics.
- `verify-dependencies.ps1 -InstallAuditTool` installs cargo-audit 0.22.2 in ignored artifacts
  and runs RustSec plus NuGet transitive checks. Missing tools, unreachable advisory sources,
  reported vulnerabilities and audit warnings fail the check. CI runs it before release draft creation.
- `package-desktop.ps1 -Version <x.y.z> -ReleaseTag <v...>` embeds the full release version.
  Stable tags require a production code-signing certificate. Optional `-CertificateThumbprint`
  and `-SignToolPath` sign the owned payload, native DLL, portable EXE, setup and generated
  uninstaller using SHA-256 and a timestamp. Public certificate trust/signatures are verified;
  self-signed production certificates are refused. No certificate is created or trusted by this script.
  The current hosted draft workflow has no signing identity configured and therefore refuses stable tags.

## Verification in this checkout

- Rust formatting and Clippy with warnings denied passed.
- All 90 native tests passed, including source-locking/ADS-copy failure safety, prepared move/undo
  recovery, legacy/new history paging, request bounds, notification-independent job waiting,
  and the disposable AppData/HKCU recycle-and-backup integration fixture.
- Seven managed regression checks passed: ordinary/deep/malformed metadata, bounded reads,
  single-flight reuse/caps and semantic release/channel ordering.
- Release WinUI builds and both portable/setup packaging paths completed. The final packaging
  build emitted NU1900 because the NuGet advisory service could not be reached; compilation succeeded.
- An unsigned local test build is under `artifacts/desktop/0.1.6`, with embedded version
  `0.1.6-hardening.1`. This test version does not change a release tag, commit or published artifact.
  It precedes the final legacy-history I/O optimization, which was verified by the native history tests.
- Portable verification passed with a fresh extraction, paths containing spaces, renamed download,
  inventory, production organizer round trip, adjacent state and normal windows with animations off/on.
- A ten-cycle normal-window run passed. After diagnostic collection, the last seven samples held
  at 1,557 handles and 80 threads; managed memory was about 2.9 MiB. This short local run did not
  show continued retention. It is not a long-duration or hardware-wide leak certification.
- Release-tooling syntax/refusal guards passed. Diff whitespace checks passed.
- Per-user setup, installed ABI 3/production organizer, uninstall, registration removal and isolated
  state preservation passed through a disposable .NET harness. The restricted PowerShell run rolled
  back on denied HKCU/Start-menu writes; the external PowerShell host stalled during initialization,
  so the same fixture checks were executed directly with .NET instead.

Publication CI reproduced the earlier collision test failure and identified a Windows rename
request that omitted the filename terminator. The variable buffer now includes that terminator
while its filename-length field still excludes it. The complete native CI checks passed after
the correction. CI also caught parallel file tests sharing a millisecond-based fixture name;
an atomic counter now keeps their directories distinct. Concurrently locked files remain a
reported per-file failure rather than being forced through a move.

## Remaining release gates and limits

The local environment could not download cargo-audit from crates.io, and later NuGet restores
reported NU1900. Publication subsequently completed the current Rust and NuGet advisory gates
successfully in GitHub Actions, together with all native/managed checks, packaging,
per-user install/uninstall, portable startup, normal-window smoke and build provenance.
The published preview is [v0.1.6-rc.1](https://github.com/wiredincorp/cleanup/releases/tag/v0.1.6-rc.1),
built from `ae319fe04d028adb5010654fd5b1e0ab94c7548b` by
[release run 40152191647](https://github.com/wiredincorp/cleanup/actions/runs/40152191647).

A trusted production certificate and timestamped signing verification are still required for a
stable public release. The signing path was inspected and its refusal checks exercised; no
production certificate was available to execute it. Generated test artifacts are unsigned previews.

Clean Windows VM testing without development runtimes, downloaded-file/MOTW behavior, UAC
flows with representative third-party uninstallers, and long-running/device-specific profiling
remain release validation work. Local tests do not stand in for those environments.

The Recycle Bin and registry removal are separate Windows operations, not an atomic transaction
with other applications. A crashed cross-volume copy can leave an incomplete destination while
retaining the original; it must be reviewed before retry. Legacy history remains readable without
migration, but reading older legacy batches still scans that legacy file. Oversized history fails
explicitly instead of exhausting memory; its original logs remain available for manual review.
