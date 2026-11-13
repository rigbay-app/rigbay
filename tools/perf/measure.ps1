# Measures what Rigbay costs in memory and CPU with its window open on a module page and after it has been in the tray for a while, one run of the
# app per trim variant (docs/perf/TRAY-MEMORY-2026-11.md has the results and how to read them). Repeatable: every run starts the app elevated (the
# manifest asks for it; UAC auto-elevates for an admin account) on its own scratch --data folder and a built-in module set, with --allow-multi, so
# a Rigbay that is running for real is never touched, and ends it by itself (--exit-after). Nothing is written outside -OutDir.
#
#   powershell -ExecutionPolicy Bypass -File tools\perf\measure.ps1 [-Set released|demo] [-Variants none,drop,...] [-Repeat 1] [-Quick]
#
# Needs tools\out\app (the published app) and tools\out\modules-released (tools\demo-modules\released\build.ps1 plus modules\tunnel\tools\demo\build-demo.ps1)
# or tools\out\modules (tools\build-demo.ps1). Module sets (made-up machines, no hardware, service, Windows setting or network is touched):
#   released  xtu, predatorsense, steam-bypass, tunnel on their test fakes, page xtu  (the owner's four modules)
#   demo      demo-fan, demo-devices, demo-lighting, page demo-fan
# Variants are the steps of the memory trim (--trim-steps): none = the app as before the change; drop = page dropped; caches; gc = heap collected and
# compacted; all = + working set trimmed. A name is a plus list ("drop+gc") or one of the words.
#
# Each run: window open on the page for -OpenSeconds, 60 s more (CPU per minute with the window open), hide to the tray, then marks at 50 s, 2 min and
# 3 min hidden (the trim runs at -TrimDelay, 60 s), show again and read the time to the first frame. The app writes one "MEM <label> ..." line per mark
# (script step mem:<label>); this script reads them back from the app's log.
[CmdletBinding()]
param(
    [ValidateSet('released', 'demo')][string]$Set = 'released',
    [string[]]$Variants = @('none', 'drop', 'drop+caches', 'drop+caches+gc', 'all'),
    [int]$Repeat = 1,
    [int]$OpenSeconds = 25,
    [int]$TrimDelay = 60,
    [string]$AppDir,
    [string]$ModulesDir,
    [string]$OutDir,
    [switch]$Quick   # shorter marks (open 10 s, +20 s, hidden 20 s / 45 s / 75 s, delay 20 s): for a look, not for the document
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$tools = Split-Path $PSScriptRoot
if (-not $AppDir) { $AppDir = Join-Path $tools 'out\app' }
if (-not $OutDir) { $OutDir = Join-Path $tools 'out\perf' }
if (-not $ModulesDir) { $ModulesDir = if ($Set -eq 'released') { Join-Path $tools 'out\modules-released' } else { Join-Path $tools 'out\modules' } }
if (-not (Test-Path (Join-Path $AppDir 'Rigbay.App.exe'))) { throw "Not built: $AppDir\Rigbay.App.exe" }
if (-not (Test-Path $ModulesDir)) { throw "No module folder: $ModulesDir" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$modules = if ($Set -eq 'released') { @('xtu', 'predatorsense', 'steam-bypass', 'tunnel') } else { @('demo-fan', 'demo-devices', 'demo-lighting') }
$page = $modules[0]
$openMore = 60; $hid1 = 50; $hid2 = 70; $hid3 = 60
if ($Quick) { $OpenSeconds = 10; $openMore = 20; $TrimDelay = 20; $hid1 = 15; $hid2 = 30; $hid3 = 30 }

function StepsOf([string]$v) { switch ($v) { 'none' { 'none' } 'all' { 'all' } default { ($v -split '[+,]' | Where-Object { $_ }) -join ',' } } }

function Run-One([string]$variant, [int]$n) {
    $tag = ($variant -replace '[^a-z0-9]+', '-') + "-$n"
    $data = Join-Path $OutDir "data-$Set-$tag"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    $steps = @($modules | ForEach-Object { "enable:$_" })
    $steps += "page:$page", "wait:$($OpenSeconds * 1000)", 'mem:open', "wait:$($openMore * 1000)", 'mem:open+60s',
        'hide', "wait:$($hid1 * 1000)", 'mem:hidden-50s', "wait:$($hid2 * 1000)", 'mem:hidden-2min', "wait:$($hid3 * 1000)", 'mem:hidden-3min',
        'show', 'wait:2500', 'mem:reopened', 'wait:20000', 'mem:reopened+20s'
    $script = $steps -join ';'
    $appArgs = @('--allow-multi', '--size', '1240x900', '--trim-delay', "$TrimDelay", '--trim-steps', (StepsOf $variant), '--script', $script, '--exit-after', '500')
    $total = $OpenSeconds + $openMore + $hid1 + $hid2 + $hid3 + 30
    Write-Host ("[{0}] variant '{1}' run {2}: about {3} s" -f $Set, $variant, $n, $total)
    $r = & (Join-Path $tools 'run-elevated.ps1') -Wait -Modules $ModulesDir -Data $data -AppDir $AppDir @appArgs
    $logs = @(Get-ChildItem (Join-Path $data 'logs') -Filter *.log -ErrorAction SilentlyContinue)
    if ($logs.Count -eq 0) { throw "no log written under $data\logs (exit: $($r -join ' '))" }
    $lines = @($logs | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 })
    $marks = @{}
    $rx = 'MEM (?<l>\S+) ws=(?<ws>[\d.]+) private=(?<pr>[\d.]+) managed=(?<mg>[\d.]+) gcheap=(?<gh>[\d.]+) gccommitted=(?<gc>[\d.]+) gcfrag=(?<gf>[\d.]+) threads=(?<th>\d+) handles=(?<hd>\d+) cpu_ms=(?<cpu>\d+) wall_ms=(?<wall>\d+)'
    foreach ($ln in $lines) {
        if ($ln -match $rx) {
            $inv = [Globalization.CultureInfo]::InvariantCulture
            $marks[$Matches.l] = [pscustomobject]@{
                Label = $Matches.l; Ws = [double]::Parse($Matches.ws, $inv); Private = [double]::Parse($Matches.pr, $inv); Managed = [double]::Parse($Matches.mg, $inv)
                GcHeap = [double]::Parse($Matches.gh, $inv); GcCommitted = [double]::Parse($Matches.gc, $inv); Threads = [int]$Matches.th; Handles = [int]$Matches.hd
                CpuMs = [double]$Matches.cpu; WallMs = [double]$Matches.wall
            }
        }
    }
    $trim = @($lines | Where-Object { $_ -match 'TRAYTRIM ' } | Select-Object -Last 1)
    $frame = @($lines | Where-Object { $_ -match 'TRAYRESTORE first frame' } | Select-Object -Last 1)
    $built = @($lines | Where-Object { $_ -match 'TRAYRESTORE page rebuilt' } | Select-Object -Last 1)
    $errs = @($lines | Where-Object { $_ -match ' (ERROR|WARN)\s' -and $_ -notmatch 'MEM ' } | Select-Object -First 5)
    function Cpu($a, $b) { if ($marks.ContainsKey($a) -and $marks.ContainsKey($b)) { $d = $marks[$b].WallMs - $marks[$a].WallMs; if ($d -gt 0) { [math]::Round(($marks[$b].CpuMs - $marks[$a].CpuMs) / $d * 100, 2) } else { $null } } else { $null } }
    function Ms($t, $rx2) { if ($t.Count -gt 0 -and $t[0] -match $rx2) { [double]$Matches.v } else { $null } }
    [pscustomobject]@{
        Set = $Set; Variant = $variant; Run = $n; Marks = $marks
        CpuOpenPct = (Cpu 'open' 'open+60s'); CpuHiddenPct = (Cpu 'hidden-2min' 'hidden-3min')
        ReopenMs = (Ms $frame 'first frame (?<v>\d+) ms'); RebuildMs = (Ms $built 'rebuilt in (?<v>\d+) ms')
        TrimLine = $(if ($trim.Count -gt 0) { ($trim[0] -replace '^.*TRAYTRIM ', '') } else { '' }); Problems = $errs; Log = $logs[0].FullName
    }
}

$results = @()
foreach ($v in $Variants) { for ($i = 1; $i -le $Repeat; $i++) { $results += Run-One $v $i } }

function F($x, $fmt = '0') { if ($null -eq $x) { '-' } else { ([double]$x).ToString($fmt, [Globalization.CultureInfo]::InvariantCulture) } }
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("# Rigbay memory, set '$Set' ($($modules -join ', ')), trim delay $TrimDelay s, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| variant | run | open WS | open private | open managed | hidden 50 s WS | hidden 2 min WS | hidden 2 min private | hidden 2 min managed | threads open / hidden | handles open / hidden | CPU % open | CPU % hidden | reopen ms | rebuild ms |')
[void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    $m = $r.Marks
    function G($label, $prop) { if ($m.ContainsKey($label)) { F $m[$label].$prop '0.0' } else { '-' } }
    $th = if ($m.ContainsKey('open') -and $m.ContainsKey('hidden-2min')) { "$($m['open'].Threads) / $($m['hidden-2min'].Threads)" } else { '-' }
    $hd = if ($m.ContainsKey('open') -and $m.ContainsKey('hidden-2min')) { "$($m['open'].Handles) / $($m['hidden-2min'].Handles)" } else { '-' }
    [void]$sb.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |' -f $r.Variant, $r.Run, (G 'open' 'Ws'), (G 'open' 'Private'), (G 'open' 'Managed'),
        (G 'hidden-50s' 'Ws'), (G 'hidden-2min' 'Ws'), (G 'hidden-2min' 'Private'), (G 'hidden-2min' 'Managed'), $th, $hd, (F $r.CpuOpenPct '0.00'), (F $r.CpuHiddenPct '0.00'), (F $r.ReopenMs), (F $r.RebuildMs)))
}
[void]$sb.AppendLine('')
foreach ($r in $results) {
    [void]$sb.AppendLine("- $($r.Variant) run $($r.Run): trim: $($r.TrimLine)")
    if ($r.Problems.Count -gt 0) { [void]$sb.AppendLine("  - log problems: " + ($r.Problems -join ' || ')) }
}
$report = Join-Path $OutDir ("measure-$Set-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
[IO.File]::WriteAllText($report, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
$sb.ToString()
"report: $report"
