<#
.SYNOPSIS
    Checks how FanaBridge's devices behave while the plugin is disabled.

.DESCRIPTION
    SimHub finds device types by scanning assemblies rather than by consulting
    the list of enabled plugins, so FanaBridge's devices are still created,
    loaded, updated and saved while the plugin is switched off. Three things
    have gone wrong in that state, and this measures all of them from one run:

      1. Device settings files were rewritten and lost their LED data.
      2. The log filled with per-frame device status changes (one session
         reached 127,145 lines).
      3. A device's settings pane came up with no tabs, and stayed that way
         until SimHub was fully restarted.

    Run -Before, follow the printed steps, then run -After. The settings and
    log checks are measured; the two UI questions can only be answered by
    looking, so it asks and records the answers alongside the evidence.

.EXAMPLE
    .\verify-disabled-plugin-behaviour.ps1 -Before
    .\verify-disabled-plugin-behaviour.ps1 -After
#>
[CmdletBinding(DefaultParameterSetName = 'After')]
param(
    [Parameter(ParameterSetName = 'Before', Mandatory)][switch]$Before,
    [Parameter(ParameterSetName = 'After',  Mandatory)][switch]$After,
    [string]$SimHubDir = 'C:\Program Files (x86)\SimHub',
    [string]$StatePath = (Join-Path $env:TEMP 'fanabridge-disabled-check.json')
)

$ErrorActionPreference = 'Stop'

$devicesDir = Join-Path $SimHubDir 'PluginsData\Common\Devices'
$logPath    = Join-Path $SimHubDir 'Logs\SimHub.txt'

function Get-DeviceSettingsSnapshot {
    if (-not (Test-Path $devicesDir)) { return @{} }
    $snap = @{}
    foreach ($f in Get-ChildItem $devicesDir -Recurse -Filter settings.json -File) {
        # _Backups holds SimHub's rolling copies; only the live files matter.
        if ($f.FullName -like '*\_Backups\*') { continue }
        $snap[$f.FullName] = @{
            Hash   = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
            Length = $f.Length
        }
    }
    return $snap
}

function Assert-SimHubClosed {
    if (Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue) {
        throw 'SimHub is running. Close it first so the log and settings files are settled.'
    }
}

if ($Before) {
    Assert-SimHubClosed

    $snapshot = Get-DeviceSettingsSnapshot
    if ($snapshot.Count -eq 0) {
        Write-Warning "No device settings files found under $devicesDir - the settings check will prove nothing."
    }

    # Record where the log ends now, so -After only reads what this test produced.
    $logLength = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }

    @{
        TakenAt   = (Get-Date).ToString('o')
        SimHubDir = $SimHubDir
        LogPath   = $logPath
        LogLength = $logLength
        Devices   = $snapshot
    } | ConvertTo-Json -Depth 5 | Set-Content $StatePath -Encoding UTF8

    Write-Host ''
    Write-Host "Recorded $($snapshot.Count) device settings file(s)." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Now do this, in order:' -ForegroundColor Cyan
    Write-Host '  1. Disable the FanaBridge plugin in SimHub, then CLOSE SimHub completely.'
    Write-Host '  2. Start SimHub. Leave it running for at least 60 seconds.'
    Write-Host '  3. Open the settings for one of your Fanatec devices.'
    Write-Host '       -> note whether it has any TABS, and what STATUS the device row shows.'
    Write-Host '  4. Re-enable FanaBridge, WITHOUT restarting SimHub.'
    Write-Host '       -> note whether the tabs appear now, and what the status shows.'
    Write-Host '  5. Close SimHub, then run this script again with -After.'
    Write-Host ''
    return
}

# ── -After ────────────────────────────────────────────────────────────────
Assert-SimHubClosed

if (-not (Test-Path $StatePath)) { throw "No baseline found at $StatePath. Run with -Before first." }
$state = Get-Content $StatePath -Raw | ConvertFrom-Json

$results = [ordered]@{}

# 1. Did any device settings file change while the plugin was off?
$now = Get-DeviceSettingsSnapshot
$changed = @()
foreach ($p in $state.Devices.PSObject.Properties) {
    $before = $p.Value
    if (-not $now.ContainsKey($p.Name)) { $changed += "DELETED  $($p.Name)"; continue }
    $after = $now[$p.Name]
    if ($after.Hash -ne $before.Hash) {
        $changed += ("CHANGED  {0}  {1} -> {2} bytes" -f $p.Name, $before.Length, $after.Length)
    }
}
$results['Settings files untouched'] = @{
    Pass   = ($changed.Count -eq 0)
    Detail = if ($changed.Count -eq 0) { "$($state.Devices.PSObject.Properties.Count) file(s) byte-identical" }
             else { $changed -join "`n           " }
}

# 2. Status-change flood.
$logText = ''
if (Test-Path $state.LogPath) {
    $fs = [System.IO.File]::Open($state.LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        # The log rotates; if it shrank, this run starts from the top.
        if ($fs.Length -ge $state.LogLength) { [void]$fs.Seek($state.LogLength, 'Begin') }
        $logText = (New-Object System.IO.StreamReader($fs)).ReadToEnd()
    } finally { $fs.Dispose() }
}

$statusLines = [regex]::Matches(
    $logText, '\[(?<ts>[\d\-]+ [\d:,]+)\] INFO - Device Status changed : (?<dev>Fanatec[^:]+): (?<state>\w+)')

$byState = $statusLines | Group-Object { $_.Groups['state'].Value } | Sort-Object Count -Descending
$span = 0.0
if ($statusLines.Count -ge 2) {
    $fmt = 'yyyy-MM-dd HH:mm:ss,fff'
    $t0 = [datetime]::ParseExact($statusLines[0].Groups['ts'].Value, $fmt, $null)
    $t1 = [datetime]::ParseExact($statusLines[$statusLines.Count - 1].Groups['ts'].Value, $fmt, $null)
    $span = ($t1 - $t0).TotalSeconds
}
$rate = if ($span -gt 1) { [math]::Round($statusLines.Count / $span, 1) } else { $statusLines.Count }

# A handful of transitions is normal (connect, disconnect, re-enable). A flood
# is the per-frame oscillation, which runs at tens of lines per second.
$results['No status-change flood'] = @{
    Pass   = ($rate -lt 5)
    Detail = "$($statusLines.Count) line(s) over $([math]::Round($span,1))s = $rate/s" +
             ($(if ($byState) { '  [' + (($byState | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ') + ']' } else { '' }))
}

# 3. Did the devices ever report Disabled? That is the state SimHub overrides,
#    and reporting it is what caused the oscillation.
$disabled = ($statusLines | Where-Object { $_.Groups['state'].Value -eq 'Disabled' }).Count
$results['Never reports Disabled'] = @{
    Pass   = ($disabled -eq 0)
    Detail = "$disabled Disabled transition(s)"
}

# 4. Devices still loaded at all while the plugin was off.
$loaded = [regex]::Matches($logText, 'INFO - Loaded device : Fanatec').Count
$results['Devices still registered'] = @{
    Pass   = ($loaded -gt 0)
    Detail = "$loaded Fanatec device(s) loaded this run"
}

Write-Host ''
Write-Host '── Measured ────────────────────────────────────────────────' -ForegroundColor Cyan
foreach ($k in $results.Keys) {
    $r = $results[$k]
    $tag = if ($r.Pass) { 'PASS' } else { 'FAIL' }
    $col = if ($r.Pass) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1,-26} {2}" -f $tag, $k, $r.Detail) -ForegroundColor $col
}

Write-Host ''
Write-Host '── Observed (only you can answer these) ────────────────────' -ForegroundColor Cyan
$tabsWhileOff  = Read-Host '  Settings pane while DISABLED - did it show tabs? (y/n/skip)'
$statusWhileOff= Read-Host '  Status shown on the device row while DISABLED (e.g. Scanning/Connected/Disabled)'
$tabsAfterOn   = Read-Host '  After re-enabling WITHOUT restart - were the tabs there? (y/n/skip)'

$report = [ordered]@{
    RanAt                   = (Get-Date).ToString('o')
    Measured                = $results
    TabsWhileDisabled       = $tabsWhileOff
    StatusWhileDisabled     = $statusWhileOff
    TabsAfterReEnable       = $tabsAfterOn
    FanatecStatusTransitions = ($statusLines | ForEach-Object {
        "$($_.Groups['ts'].Value)  $($_.Groups['dev'].Value.Trim()) -> $($_.Groups['state'].Value)" } |
        Select-Object -First 40)
}
$out = Join-Path $env:TEMP ('fanabridge-disabled-report-{0:yyyyMMdd-HHmmss}.json' -f (Get-Date))
$report | ConvertTo-Json -Depth 6 | Set-Content $out -Encoding UTF8

Write-Host ''
Write-Host "Report written to $out" -ForegroundColor Green
if ($statusWhileOff -match 'Connected') {
    Write-Host ''
    Write-Host 'The row showed Connected with no plugin running - that is the one symptom' -ForegroundColor Yellow
    Write-Host 'still unexplained, and the log above says what state we actually reported.' -ForegroundColor Yellow
}
Write-Host ''
