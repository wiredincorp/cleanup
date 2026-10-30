param([Parameter(Mandatory)] [string] $ExecutablePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$run = Join-Path $root ('artifacts/ui-smoke/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$report = Join-Path $run 'report.json'
$process = Start-Process -FilePath $exe -ArgumentList @('--ui-smoke', '--report', ('"' + $report + '"'), '--local-app-data-root', ('"' + (Join-Path $run 'profile') + '"'), '--fixture-dir', ('"' + (Join-Path $run 'fixtures') + '"')) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(90000)) { Stop-Process -Id $process.Id -Force; throw 'Normal-window smoke test timed out.' }
    if (-not (Test-Path -LiteralPath $report)) { throw 'Normal-window smoke test did not produce a report. Close any existing Cleanup instance before running this check.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.passed -or -not $result.normalMainWindow -or $result.abiVersion -ne 3 -or $result.navigationCycles -ne 10 -or $result.organizer.restored -ne 1 -or $result.photos.restored -ne 1 -or $result.photos.captured -ne '2021-06-15') { throw "Normal-window smoke failed: $($result.error)" }
    Write-Output "Normal-window navigation, production organizer and photo sorting passed. Samples: $report"
} finally { $process.Dispose() }
