# Read-only UI regression: checks navigation/title positions through page changes.
# Changes only the app's selection, search text, page and window size.
param([switch] $Compact)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ui-automation.ps1')
Connect-CleanupUi (Get-Process Cleanup | Select-Object -First 1 -ExpandProperty Id)
$transform = $script:UiWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
$transform.Resize($(if ($Compact) {900} else {1240}), $(if ($Compact) {580} else {800}))
Select-CleanupUi Applications
Set-CleanupUiText 'Search apps' ''
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $apps = @(Find-CleanupUi -Name '*' -Type ListItem | Where-Object { $_.Current.Name -notin @('Core','Applications','Organize','Photos','Videos','Folders','History','Settings','Patch notes') })
    if ($apps.Count -ge 2) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if ($apps.Count -lt 2) { throw 'Inventory needs at least two rows before this check.' }
$measurements = @()
$baseline = @{}
foreach ($selection in @('single','multiple','empty')) {
    Select-CleanupUi Applications
    if ($selection -eq 'single') { $apps[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    elseif ($selection -eq 'multiple') { $apps[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection() }
    else { Invoke-CleanupUi 'Clear application selection' }
foreach ($page in @('Applications','Organize','History','Settings','Applications')) {
    Select-CleanupUi $page
    Start-Sleep -Milliseconds 350
    foreach ($name in @('Applications','Organize','History','Settings')) {
        $element = Find-CleanupUi -Name $name -Type ListItem | Select-Object -First 1
        if (-not $element) { throw "Missing navigation: $name" }
        $window = $script:UiWindow.Current.BoundingRectangle
        $expectedHeight = if ($Compact) {580} else {800}
        if ([math]::Abs($window.Height-$expectedHeight) -gt 2) { throw 'Window was resized during verification; rerun with the window left at the requested size.' }
        $rect = $element.Current.BoundingRectangle
        $position = [ordered]@{selection=$selection; page=$page; item=$name; x=[math]::Round($rect.Left-$window.Left,1); y=[math]::Round($rect.Top-$window.Top,1)}
        $measurements += [pscustomobject]$position
        if (-not $baseline.ContainsKey($name)) { $baseline[$name] = $position }
        elseif ([math]::Abs($baseline[$name].x-$position.x) -gt 1 -or [math]::Abs($baseline[$name].y-$position.y) -gt 1) {
            $measurements | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot '..\artifacts\sidebar-layout-result.json')
            throw "$name shifted on $page from $($baseline[$name].x),$($baseline[$name].y) to $($position.x),$($position.y)."
        }
    }
}
}
$kind = if ($Compact) { 'compact' } else { 'normal' }
$measurements | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot "..\artifacts\sidebar-layout-$kind.json")
$transform.Resize(1240,800)
Write-Output "Sidebar positions unchanged across all pages with single, multiple and empty selection ($kind)."
