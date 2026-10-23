param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version = '0.1.2',
    [string] $ExecutablePath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sourceExe = if ($ExecutablePath) { (Resolve-Path -LiteralPath $ExecutablePath).Path } else { Join-Path $root "artifacts/desktop/$Version/Cleanup.exe" }
if (-not (Test-Path $sourceExe)) { throw "Portable app was not found: $sourceExe" }

$run = Join-Path $root ('artifacts/portable-location-qa/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$previousEnvironment = @{
    BundleExtractBase = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    DotnetRoot = $env:DOTNET_ROOT
    MultilevelLookup = $env:DOTNET_MULTILEVEL_LOOKUP
}
$sourceLength = (Get-Item -LiteralPath $sourceExe).Length
$succeeded = $false

function New-LegacyState([string] $localAppDataRoot, [string] $marker) {
    $state = Join-Path $localAppDataRoot 'Cleanup'
    $history = Join-Path $state 'history'
    New-Item -ItemType Directory -Path $history -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $state 'settings.json'), "`"$marker`"")
    [IO.File]::WriteAllText((Join-Path $history 'migration-fixture.txt'), $marker)
    [IO.File]::WriteAllText((Join-Path $state 'last-error.txt'), 'diagnostic file must not migrate')
    [IO.File]::WriteAllText((Join-Path $state 'phase0-persistence-probe.jsonl'), 'test journal must not migrate')
}

function Invoke-PortableSelfTest(
    [string] $name,
    [string] $portableDirectory,
    [string] $localAppDataRoot,
    [string] $expectedState
) {
    New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
    $exe = Join-Path $portableDirectory 'Cleanup.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $exe

    $reportPath = Join-Path $run "$name.json"
    $fixtureDirectory = Join-Path $run "$name fixture files"
    $extractBase = Join-Path $run "$name fresh extraction with spaces"
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractBase
    # A self-contained bundle must launch from its embedded runtime even when
    # no machine-wide .NET runtime can be discovered.
    $env:DOTNET_ROOT = Join-Path $run 'No separate dotnet runtime'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'

    $process = Start-Process -FilePath $exe -ArgumentList @(
        '--selftest', '--report', ('"' + $reportPath + '"'),
        '--fixture-dir', ('"' + $fixtureDirectory + '"'),
        '--local-app-data-root', ('"' + $localAppDataRoot + '"')
    ) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "$name portable startup/storage self-test timed out."
    }
    if (-not (Test-Path $reportPath)) { throw "$name portable app did not write a self-test report (exit $($process.ExitCode))." }

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.fatal -or $report.errors.Count -or $report.process.packaged -ne $false -or -not $report.process.portableSingleFile -or $report.nativeCore.abiVersion -ne 2) {
        throw "$name portable startup, single-file detection, or native ABI verification failed."
    }
    if ($report.process.startupToWindowLoadedMs -le 0 -or -not $report.inventory.summary -or $report.inventory.summary.sourceErrors.Count) {
        throw "$name WinUI startup or installed-app inventory scan failed."
    }
    if (-not $report.fileMoves.moved -or -not $report.fileMoves.movedBack -or -not $report.fileMoves.existingFileUnchanged) {
        throw "$name portable file move/undo storage verification failed."
    }
    if ([IO.Path]::GetFullPath($report.process.localStateDirectory) -ne [IO.Path]::GetFullPath($expectedState)) {
        throw "$name selected the wrong writable state location: $($report.process.localStateDirectory)"
    }
    if (-not [IO.Path]::GetFullPath($report.persistence.journalPath).StartsWith([IO.Path]::GetFullPath($expectedState), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$name persistence did not write inside its selected state location."
    }
    if ($report.nativeCore.environment.elevated) {
        throw "$name portable self-test unexpectedly ran elevated. Run this location test as a standard user."
    }

    $nativeLibraryPath = [IO.Path]::GetFullPath($report.nativeCore.environment.nativeLibraryPath)
    $extractPrefix = [IO.Path]::GetFullPath($extractBase).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    if (-not $nativeLibraryPath.StartsWith($extractPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$name Rust core loaded from outside its fresh self-extraction directory."
    }
    foreach ($required in @('cleanup_native.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'Cleanup.pri')) {
        if (-not (Test-Path (Join-Path (Split-Path $nativeLibraryPath) $required))) {
            throw "$name self-extracted dependency is missing: $required"
        }
    }

    [pscustomobject]@{
        Name = $name
        Executable = $exe
        StateDirectory = $report.process.localStateDirectory
        Elevated = [bool]$report.nativeCore.environment.elevated
        NativeLibrary = $nativeLibraryPath
        StartupToWindowLoadedMs = $report.process.startupToWindowLoadedMs
        FileMoveUndoPassed = [bool]($report.fileMoves.moved -and $report.fileMoves.movedBack -and $report.fileMoves.existingFileUnchanged)
    }
}

try {
    $writableDirectory = Join-Path $run 'Writable USB-like path with spaces'
    $writableProfile = Join-Path $run 'Writable test profile with spaces'
    $writableMarker = 'portable migration fixture'
    New-LegacyState $writableProfile $writableMarker
    $writableState = Join-Path $writableDirectory 'CleanupData'
    $writableResult = Invoke-PortableSelfTest 'writable path' $writableDirectory $writableProfile $writableState

    $copiedSettings = Get-Content -LiteralPath (Join-Path $writableState 'settings.json') -Raw
    $copiedHistory = Get-Content -LiteralPath (Join-Path $writableState 'history/migration-fixture.txt') -Raw
    if ($copiedSettings.Trim() -ne '"' + $writableMarker + '"' -or $copiedHistory -ne $writableMarker) {
        throw 'Writable portable mode did not carry forward existing settings and history.'
    }
    if (Test-Path (Join-Path $writableState 'last-error.txt')) { throw 'Portable migration copied a diagnostic log.' }
    if ((Get-Content -LiteralPath (Join-Path $writableProfile 'Cleanup/settings.json') -Raw).Trim() -ne '"' + $writableMarker + '"') {
        throw 'Portable migration changed the original LocalAppData settings.'
    }

    $readOnlyDirectory = Join-Path $run 'Read-only USB-like path with spaces'
    $readOnlyProfile = Join-Path $run 'Read-only test profile with spaces'
    $readOnlyMarker = 'fallback profile fixture'
    New-Item -ItemType Directory -Path $readOnlyDirectory -Force | Out-Null
    $readOnlyExe = Join-Path $readOnlyDirectory 'Cleanup.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $readOnlyExe
    New-LegacyState $readOnlyProfile $readOnlyMarker

    $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($run))
    if ($volume.DriveFormat -ne 'NTFS') {
        throw "Read-only permission simulation requires NTFS; this workspace is on $($volume.DriveFormat)."
    }
    $originalAcl = Get-Acl -LiteralPath $readOnlyDirectory
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $deniedRights = [Security.AccessControl.FileSystemRights]::CreateFiles -bor
        [Security.AccessControl.FileSystemRights]::CreateDirectories
    $denyWrite = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        $deniedRights,
        [Security.AccessControl.InheritanceFlags]::None,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Deny)
    $acl = Get-Acl -LiteralPath $readOnlyDirectory
    $acl.AddAccessRule($denyWrite)
    Set-Acl -LiteralPath $readOnlyDirectory -AclObject $acl

    try {
        $writeProbe = Join-Path $readOnlyDirectory 'write-must-be-denied.tmp'
        $driveIsWritable = $false
        try {
            [IO.File]::WriteAllText($writeProbe, 'probe')
            $driveIsWritable = $true
        }
        catch [UnauthorizedAccessException] { }
        if ($driveIsWritable) {
            Remove-Item -LiteralPath $writeProbe -Force
            throw 'The ACL did not make the USB-like path read-only.'
        }

        $readOnlyState = Join-Path $readOnlyProfile 'Cleanup'
        $readOnlyResult = Invoke-PortableSelfTest 'write-denied path' $readOnlyDirectory $readOnlyProfile $readOnlyState
        if (Test-Path (Join-Path $readOnlyDirectory 'CleanupData')) {
            throw 'Read-only portable mode left an unwritable CleanupData directory behind.'
        }
        if ((Get-Content -LiteralPath (Join-Path $readOnlyState 'settings.json') -Raw).Trim() -ne '"' + $readOnlyMarker + '"') {
            throw 'Read-only fallback changed the existing LocalAppData settings.'
        }
    }
    finally {
        Set-Acl -LiteralPath $readOnlyDirectory -AclObject $originalAcl
    }

    $writableResult | Format-List | Out-String | Write-Output
    $readOnlyResult | Format-List | Out-String | Write-Output

    $removableDrives = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq [IO.DriveType]::Removable -and $_.IsReady })
    if ($removableDrives.Count -eq 0) {
        Write-Output 'SKIPPED physical USB test: no removable drive is attached; writable and write-denied path behavior passed in isolated local folders.'
    }
    else {
        $drive = $removableDrives[0]
        if ($drive.AvailableFreeSpace -lt ($sourceLength + 180MB)) {
            Write-Output "SKIPPED physical USB test: removable drive $($drive.Name) does not have enough free space for the EXE and extracted runtime."
        }
        else {
            $usbDirectory = Join-Path $drive.RootDirectory.FullName ('Cleanup USB QA with spaces ' + [guid]::NewGuid().ToString('N'))
            $usbProfile = Join-Path $run 'USB test profile with spaces'
            $usbMarker = 'USB path migration fixture'
            try {
                New-LegacyState $usbProfile $usbMarker
                $usbState = Join-Path $usbDirectory 'CleanupData'
                $usbResult = Invoke-PortableSelfTest 'removable drive path' $usbDirectory $usbProfile $usbState
                $usbResult | Format-List | Out-String | Write-Output
                Write-Output 'Passed: launched Cleanup.exe from a removable-drive folder with spaces.'
            }
            catch [UnauthorizedAccessException] {
                Write-Output "SKIPPED physical USB test: no write access to removable drive $($drive.Name); ACL-based write-denied fallback passed."
            }
            finally {
                if (Test-Path $usbDirectory) { Remove-Item -LiteralPath $usbDirectory -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
    }

    Write-Output 'Passed: standard-user portable launch, writable adjacent data and migration, write-denied fallback, paths with spaces, and bundled runtime extraction.'
    $succeeded = $true
}
finally {
    if ($null -eq $previousEnvironment.BundleExtractBase) { Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue }
    else { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousEnvironment.BundleExtractBase }
    if ($null -eq $previousEnvironment.DotnetRoot) { Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue }
    else { $env:DOTNET_ROOT = $previousEnvironment.DotnetRoot }
    if ($null -eq $previousEnvironment.MultilevelLookup) { Remove-Item Env:DOTNET_MULTILEVEL_LOOKUP -ErrorAction SilentlyContinue }
    else { $env:DOTNET_MULTILEVEL_LOOKUP = $previousEnvironment.MultilevelLookup }
    if ($succeeded -and (Test-Path $run)) {
        Remove-Item -LiteralPath $run -Recurse -Force
    }
    elseif (Test-Path $run) {
        Write-Output "Portable location test artifacts preserved for diagnosis: $run"
    }
}
