param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version = '0.1.2',
    [string] $CompilerPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$output = Join-Path $RepoRoot "artifacts/desktop/$Version"
$publish = Join-Path $output ('setup-payload-' + [guid]::NewGuid().ToString('N'))
$portablePublish = Join-Path $output ('portable-publish-' + [guid]::NewGuid().ToString('N'))
if (-not $CompilerPath) { $CompilerPath = & (Join-Path $PSScriptRoot 'get-inno-setup.ps1') }
$project = Join-Path $RepoRoot 'src/frontend/Cleanup.csproj'

# Build the Rust core once with the static MSVC CRT. It was the only shipped
# binary importing VCRUNTIME140.dll; Windows App SDK binaries use the hybrid
# CRT and import only the Universal CRT that ships with Windows 10/11. Both
# Setup and the portable EXE therefore need no Visual C++ redistributable.
$previousRustFlags = $env:RUSTFLAGS
try {
    $env:RUSTFLAGS = (@($previousRustFlags, '-C target-feature=+crt-static') | Where-Object { $_ }) -join ' '
    cargo build --manifest-path (Join-Path $RepoRoot 'src/rust/Cargo.toml') --locked --release -p cleanup-ffi
    if ($LASTEXITCODE -ne 0) { throw 'Rust core build with the static MSVC CRT failed.' }
} finally {
    if ($null -eq $previousRustFlags) { Remove-Item Env:RUSTFLAGS -ErrorAction SilentlyContinue }
    else { $env:RUSTFLAGS = $previousRustFlags }
}
function Assert-NoVcRuntimeImport([string] $Directory) {
    foreach ($binary in Get-ChildItem $Directory -Recurse -File -Include *.dll, *.exe) {
        $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($binary.FullName))
        if ($text -match '(?i)(vcruntime140|msvcp140|concrt140|vccorlib140)[_0-9a-z]*\.dll') {
            throw "$($binary.Name) still depends on the Visual C++ redistributable ($($Matches[0]))."
        }
    }
}

# Setup fallback: framework-free folder publish installed per user by Inno Setup.
dotnet restore $project -r win-x64 -p:Platform=x64 -p:CleanupDesktop=true -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Locked restore for the Setup fallback failed.' }
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:SkipNativeBuild=true `
    -p:Platform=x64 -p:CleanupDesktop=true -p:RestoreLockedMode=true -p:DebugSymbols=false -p:DebugType=None "-p:Version=$Version" "-p:PublishDir=$publish\"
if ($LASTEXITCODE -ne 0) { throw 'Self-contained desktop publish failed.' }
$runtimeRoot = Join-Path $RepoRoot 'src/frontend/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64'
Copy-Item -LiteralPath (Join-Path $runtimeRoot 'Cleanup.pri') -Destination $publish
foreach ($folder in @('App','Views','Controls')) {
    Copy-Item -LiteralPath (Join-Path $runtimeRoot $folder) -Destination $publish -Recurse
}
foreach ($required in @('Cleanup.exe','cleanup_native.dll','Microsoft.UI.Xaml.dll','coreclr.dll','release.json','Cleanup.pri')) {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Required desktop payload is missing: $required" }
}
Assert-NoVcRuntimeImport $publish
# Include the license and notice files from the exact restored runtime packages.
$assets = Get-Content (Join-Path $RepoRoot 'src/frontend/obj/project.assets.json') -Raw | ConvertFrom-Json
$cache = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$notices = Join-Path $publish 'Notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($library in $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -match '^(Microsoft.WindowsAppSDK\.[^/]+|CommunityToolkit.Mvvm)/' }) {
    $package = Join-Path $cache $library.ToLowerInvariant()
    foreach ($notice in Get-ChildItem $package -File | Where-Object { $_.Name -match '(?i)license|notice' }) {
        $name = $library.Replace('/', '-') + '-' + $notice.Name
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices $name)
    }
}
# Runtime packs are download dependencies, not entries in assets.libraries.
$runtimeConfig = Get-Content (Join-Path $publish 'Cleanup.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (-not $runtimeVersion) { throw 'The published app must include a self-contained .NET runtime.' }
$runtimePackage = Join-Path $cache "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $runtimePackage $name) -Destination (Join-Path $notices "Microsoft.NETCore.App-$runtimeVersion-$name")
}
$innoLicense = Join-Path (Split-Path $CompilerPath) 'license.txt'
if (Test-Path $innoLicense) { Copy-Item -LiteralPath $innoLicense -Destination (Join-Path $notices 'Inno-Setup-license.txt') }
$metadata = cargo metadata --manifest-path (Join-Path $RepoRoot 'src/rust/Cargo.toml') --format-version 1 --locked | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Rust dependency metadata failed.' }
foreach ($package in $metadata.packages | Where-Object source) {
    foreach ($notice in Get-ChildItem (Split-Path $package.manifest_path) -File | Where-Object { $_.Name -match '(?i)license|notice|copyright' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices "$($package.name)-$($package.version)-$($notice.Name)")
    }
}
& $CompilerPath /Qp "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$output" (Join-Path $RepoRoot 'packaging/cleanup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup EXE build failed.' }

# Primary download: a compressed .NET single-file bundle. The .NET runtime host
# is linked into Cleanup.exe; WinUI/Windows App SDK native files, PRI/XBF
# resources, and the Rust core are extracted to %TEMP%\.net\Cleanup\<bundle id>
# on first launch. Writable portable folders keep app data in CleanupData beside
# the EXE; read-only locations fall back to %LOCALAPPDATA%\Cleanup. WinUI generated
# intermediates differ for single-file builds, so reset them first; the extra
# single-file tooling package is pinned in its own lock file.
dotnet clean $project -c Release -p:Platform=x64 -p:CleanupDesktop=true
if ($LASTEXITCODE -ne 0) { throw 'Clean build-state reset before portable publish failed.' }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:SkipNativeBuild=true `
    -p:Platform=x64 -p:CleanupDesktop=true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:NuGetLockFilePath=packages.portable.lock.json -p:RestoreLockedMode=true -p:DebugSymbols=false -p:DebugType=None "-p:Version=$Version" "-p:PublishDir=$portablePublish\"
if ($LASTEXITCODE -ne 0) { throw 'Self-extracting portable publish failed.' }
$bundleFiles = @(Get-ChildItem $portablePublish -File)
if ($bundleFiles.Count -ne 1 -or $bundleFiles[0].Name -ne 'Cleanup.exe') {
    throw 'Portable publish must produce only the downloadable Cleanup.exe.'
}
# A compressed bundle of the same app must be smaller than the uncompressed
# Setup payload; a larger bundle means stray build content was swept into it.
$payloadBytes = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum
if ($bundleFiles[0].Length -ge $payloadBytes / 2) {
    throw "Portable Cleanup.exe is unexpectedly large ($([math]::Round($bundleFiles[0].Length / 1MB)) MB); check for stray bundled content."
}
Copy-Item -LiteralPath $bundleFiles[0].FullName -Destination (Join-Path $output 'Cleanup.exe') -Force
$noticesArchive = Join-Path $output 'Cleanup-Notices.zip'
Compress-Archive -Path (Join-Path $publish 'Notices/*') -DestinationPath $noticesArchive -CompressionLevel Optimal -Force
Remove-Item -LiteralPath $publish, $portablePublish -Recurse -Force

Get-ChildItem $output -File | Where-Object { $_.Name -eq 'Cleanup.exe' -or $_.Name -eq 'Cleanup-Notices.zip' -or $_.Name -like '*-setup.exe' } | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Get-ChildItem $output -File | Select-Object FullName,Length
