param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version = '0.1.2',
    [string] $ExecutablePath,
    # Hosted CI runners always run as administrator; local checks must not.
    [switch] $AllowElevated
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = if ($ExecutablePath) { (Resolve-Path -LiteralPath $ExecutablePath).Path } else { Join-Path $root "artifacts/desktop/$Version/Cleanup.exe" }
if (-not (Test-Path $exe)) { throw "Portable app was not found: $exe" }
$run = Join-Path $root ('artifacts/portable-qa/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$reportPath = Join-Path $run 'launch.json'
$fixtures = Join-Path $run 'moves'
$portableDirectory = Join-Path $run 'Portable copy with spaces'
New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
$portableExe = Join-Path $portableDirectory 'Cleanup.exe'
Copy-Item -LiteralPath $exe -Destination $portableExe
# Keep the profile fallback isolated from the interactive user's real LocalAppData.
$localAppDataRoot = Join-Path $run 'Local profile with spaces'
# Extract into an empty directory so this run exercises a true first launch
# rather than reusing a cached extraction under %TEMP%\.net.
$extractBase = Join-Path $run 'Fresh extraction with spaces'
$previousExtractBase = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractBase
    $process = Start-Process -FilePath $portableExe -ArgumentList @(
        '--selftest', '--report', ('"' + $reportPath + '"'), '--fixture-dir', ('"' + $fixtures + '"'),
        '--local-app-data-root', ('"' + $localAppDataRoot + '"')
    ) -WindowStyle Hidden -PassThru
} finally {
    if ($null -eq $previousExtractBase) { Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue }
    else { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractBase }
}
if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw 'Portable app startup/storage self-test timed out.'
}
if (-not (Test-Path $reportPath)) { throw "Portable app did not write its self-test report (exit $($process.ExitCode))." }
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if ($report.fatal -or $report.errors.Count -or $report.process.packaged -ne $false -or $report.nativeCore.abiVersion -ne 2) {
    throw 'Portable startup, unpackaged state, or native ABI verification failed.'
}
if ($report.process.startupToWindowLoadedMs -le 0 -or -not $report.inventory.summary -or $report.inventory.summary.sourceErrors.Count) {
    throw 'WinUI window startup or installed-app inventory scan failed.'
}
if (-not $report.fileMoves.moved -or -not $report.fileMoves.movedBack -or -not $report.fileMoves.existingFileUnchanged) {
    throw 'Portable file move/undo storage verification failed.'
}
if (-not $report.process.portableSingleFile) {
    throw 'The single-file download was not detected as the portable bundle.'
}
$expectedState = Join-Path $portableDirectory 'CleanupData'
if ([IO.Path]::GetFullPath($report.process.localStateDirectory) -ne [IO.Path]::GetFullPath($expectedState)) {
    throw 'Portable app data is not stored beside Cleanup.exe when that folder is writable.'
}
if (-not [IO.Path]::GetFullPath($report.persistence.journalPath).StartsWith([IO.Path]::GetFullPath($expectedState), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Portable app persistence did not write inside its adjacent CleanupData folder.'
}
if ($report.nativeCore.environment.elevated -and -not $AllowElevated) { throw 'Portable app self-test unexpectedly ran elevated.' }
# The .NET runtime is linked into the single-file host, so coreclr.dll is not
# extracted; WinUI and the Rust core must load from the fresh extraction.
$extractDirectory = Split-Path $report.nativeCore.environment.nativeLibraryPath
if (-not [IO.Path]::GetFullPath($extractDirectory).StartsWith([IO.Path]::GetFullPath($extractBase), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Rust core loaded from outside the fresh extraction directory: $extractDirectory"
}
foreach ($required in @('cleanup_native.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'Cleanup.pri')) {
    if (-not (Test-Path (Join-Path $extractDirectory $required))) {
        throw "A self-extracted portable dependency is missing: $required"
    }
}
foreach ($binary in Get-ChildItem $extractDirectory -Recurse -File -Include *.dll, *.exe) {
    $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($binary.FullName))
    if ($text -match '(?i)(vcruntime140|msvcp140|concrt140|vccorlib140)[_0-9a-z]*\.dll') {
        throw "$($binary.Name) depends on the Visual C++ redistributable ($($Matches[0]))."
    }
}
# Browsers save a repeat download as "Cleanup (1).exe". WinUI must still find
# the app's resources when the executable has a different name.
$renamedDirectory = Join-Path $run 'Renamed download'
New-Item -ItemType Directory -Path $renamedDirectory -Force | Out-Null
$renamedExe = Join-Path $renamedDirectory 'Cleanup (1).exe'
Copy-Item -LiteralPath $exe -Destination $renamedExe
$renamedReport = Join-Path $run 'renamed-launch.json'
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractBase
    $renamed = Start-Process -FilePath $renamedExe -ArgumentList @(
        '--selftest', '--report', ('"' + $renamedReport + '"'), '--fixture-dir', ('"' + (Join-Path $run 'renamed-moves') + '"'),
        '--local-app-data-root', ('"' + $localAppDataRoot + '"')
    ) -WindowStyle Hidden -PassThru
} finally {
    if ($null -eq $previousExtractBase) { Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue }
    else { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractBase }
}
if (-not $renamed.WaitForExit(60000)) {
    Stop-Process -Id $renamed.Id -Force -ErrorAction SilentlyContinue
    throw 'The renamed portable copy timed out during startup.'
}
if (-not (Test-Path $renamedReport)) { throw "The renamed portable copy did not start (exit $($renamed.ExitCode))." }
$renamedResult = Get-Content $renamedReport -Raw | ConvertFrom-Json
if ($renamedResult.fatal -or $renamedResult.process.startupToWindowLoadedMs -le 0) {
    throw 'The renamed portable copy failed to load its window.'
}
# The self-test opens a test window, not the real one. Open the real main
# window as a person would, with Windows animation effects both off and on:
# PCs set for best performance, VMs and remote sessions turn them off.
# Packaged developer builds use their own single-instance name, so only an
# unpackaged copy would take the launch over.
if (Get-Process -Name Cleanup -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$env:ProgramFiles\WindowsApps\*" }) {
    throw 'Close Cleanup before verifying; a running copy would take this launch over.'
}
Add-Type -Namespace CleanupVerify -Name Spi -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
'@
$getAnimations = 0x1042; $setAnimations = 0x1043
$originalAnimations = 0
[void][CleanupVerify.Spi]::SystemParametersInfo($getAnimations, 0, [ref]$originalAnimations, 0)
try {
    foreach ($animations in 0, 1) {
        [void][CleanupVerify.Spi]::SystemParametersInfo($setAnimations, 0, [IntPtr]$animations, 2)
        $mainDirectory = Join-Path $run "Main window animations $animations"
        New-Item -ItemType Directory -Path $mainDirectory -Force | Out-Null
        $mainExe = Join-Path $mainDirectory 'Cleanup.exe'
        Copy-Item -LiteralPath $exe -Destination $mainExe
        $main = Start-Process -FilePath $mainExe -PassThru
        try {
            Start-Sleep -Seconds 15
            $failure = Join-Path $mainDirectory 'CleanupData/last-error.txt'
            if (Test-Path $failure) {
                throw "The main window failed to open with animations $(('off', 'on')[$animations]):`n$(Get-Content $failure -Raw)"
            }
            if ($main.HasExited) { throw "The main window closed on its own with animations $(('off', 'on')[$animations]) (exit $($main.ExitCode))." }
        } finally {
            Stop-Process -Id $main.Id -Force -ErrorAction SilentlyContinue
            $main.WaitForExit(10000) | Out-Null
        }
    }
} finally {
    [void][CleanupVerify.Spi]::SystemParametersInfo($setAnimations, 0, [IntPtr]$originalAnimations, 2)
}
Write-Output 'Passed: direct Cleanup.exe first launch from a path with spaces, fresh self-extraction of WinUI/Windows App SDK/Rust files, no Visual C++ redistributable dependency, adjacent portable data, inventory scan, disposable file move/undo, launch as a renamed download, and the real main window with animation effects off and on.'
Write-Output "Private verification report: $reportPath"
