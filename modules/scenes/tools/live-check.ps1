# Live check of the Game scenes module in YOUR OWN Rigbay instance (own --data folder, --allow-multi, never the owner's Rigbay).
#
#   tools\build.ps1                      (the app and the demo modules; this script needs tools\out\app and tools\out\modules-test)
#   dotnet build modules\scenes\src\Rigbay.Scenes -c Release
#   powershell -ExecutionPolicy Bypass -File modules\scenes\tools\live-check.ps1 [-Restart]
#
# What it does:
#   1. lays out a scratch modules folder: the demo module demo-plan-target (it changes a number in its own memory, nothing else) and this module;
#   2. seeds one scene: "scenesgame.exe" starts it; steps: demo-plan-target set 5 (undo recorded) and demo-plan-target mark on (undo: mark off);
#   3. starts the app elevated with --allow-multi, its own --data, a window script that presses Approve (the harness answers the host's plan question) and takes shots;
#   4. starts and stops "scenesgame.exe", a harmless copy of ping.exe with a name of its own, from this script;
#   5. reads the app's log and writes a report: approval, start, notification, end (after the grace), undo.
# With -Restart it then does the case "Rigbay restarted while the game runs": the app is stopped (elevated Stop-Process of OUR instance only) while the scene runs,
# started again on the same data, and the report shows that the open run was adopted and ended when the game ended.
# No real module verb is used (no XTU, Steam, tunnel or PredatorSense), nothing here touches hardware, nothing talks to the owner's Rigbay, no real-screen capture
# (the shots are of the instance's own window: --shot-mode rtb).
param(
    [string]$OutDir,
    [int]$ExitAfterMs = 130000,
    [switch]$Restart
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$tools = Join-Path $repo 'tools'
if (-not $OutDir) { $OutDir = Join-Path $tools 'out\live-scenes' }
$data = Join-Path $OutDir 'data'
$mods = Join-Path $OutDir 'modules'
$shots = Join-Path $OutDir 'shots'
$game = Join-Path $OutDir 'game\scenesgame.exe'
$report = Join-Path $OutDir 'report.txt'
$build = Join-Path $repo 'modules\scenes\src\Rigbay.Scenes\bin\Release\net8.0'
$target = Join-Path $tools 'out\modules-test\demo-plan-target'
$log = Join-Path $data 'logs\rigbay.log'

foreach ($p in @((Join-Path $tools 'out\app\Rigbay.App.exe'), (Join-Path $build 'Rigbay.Scenes.dll'), (Join-Path $target 'module.json'))) { if (-not (Test-Path $p)) { throw "missing: $p (see the header of this script)" } }

# --- scratch folders (the host keeps approved plans in an admin-only folder inside --data, so it is removed by the elevated helper)
if (Test-Path $data) { & (Join-Path $tools 'remove-elevated.ps1') $data | Out-Null }
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $data, $mods, $shots, (Split-Path $game) | Out-Null
Copy-Item $target (Join-Path $mods 'demo-plan-target') -Recurse
$sc = Join-Path $mods 'scenes'
New-Item -ItemType Directory -Force $sc | Out-Null
Copy-Item (Join-Path $build 'Rigbay.Scenes.dll'), (Join-Path $build 'module.json') $sc
Copy-Item (Join-Path $build 'lang') $sc -Recurse
Get-ChildItem $build -Filter 'icon*.svg' | Copy-Item -Destination $sc
Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $game

# --- the seed: one scene, switched on, not approved
$settingsDir = Join-Path $data 'modules\scenes'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$seed = @'
{ "version": 1, "data": { "scenes": [ {
  "id": "scene-live", "name": "Live check", "enabled": true, "games": [ "scenesgame.exe" ],
  "steps": [
    { "module": "demo-plan-target", "verb": "set", "args": [ "5" ], "undo": "recorded" },
    { "module": "demo-plan-target", "verb": "mark", "args": [ "on" ], "undo": "fixed", "undoModule": "demo-plan-target", "undoVerb": "mark", "undoArgs": [ "off" ] }
  ] } ] } }
'@
[System.IO.File]::WriteAllText((Join-Path $settingsDir 'settings.json'), $seed, (New-Object System.Text.UTF8Encoding($false)))

# --- helpers
function Start-App([string]$script, [string]$shotName, [int]$exitAfter) {
    $extra = @('--allow-multi', '--script', $script, '--shot', (Join-Path $shots $shotName), '--shot-mode', 'rtb', '--size', '1240x1500', '--theme', 'dark', '--exit-after', "$exitAfter")
    $out = & (Join-Path $tools 'run-elevated.ps1') -Data $data -Modules $mods @extra
    Write-Host "$out"
    if ("$out" -match 'pid (\d+)') { return [int]$Matches[1] }
    throw "could not start the app: $out"
}
function Stop-OurApp([int]$appPid) {
    # an elevated process cannot be stopped from a normal shell; this stops only the pid of OUR instance (never stop-elevated.ps1: it ends every Rigbay.App, the owner's too)
    Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile', '-Command', "Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue"
}
function Log-Count { if (Test-Path $log) { return @(Get-Content $log -Encoding utf8).Count } return 0 }
function Wait-Log([string]$pattern, [int]$seconds, [int]$after = 0) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        if (Test-Path $log) {
            $lines = @(Get-Content $log -Encoding utf8)
            if ($lines.Count -gt $after -and (@($lines[$after..($lines.Count - 1)] | Where-Object { $_ -match $pattern }).Count -gt 0)) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Start-Game { Start-Process -FilePath $game -ArgumentList '-t', '127.0.0.1' -WindowStyle Hidden -PassThru }
$result = [ordered]@{}

# --- phase 1: approval, start, end
$script1 = 'enable:demo-plan-target;enable:scenes;page:scenes;wait:2500;expand-all;wait:800;shot:a-needs-approval;approve:true;invoke:Approve;wait:2500;shot:b-approved;wait:12000;shot:c-running;wait:12000;shot:d;wait:12000;shot:e-ended;wait:10000;shot:f'
$app1 = Start-App $script1 'live.png' $ExitAfterMs
$result.first = if (Wait-Log 'Rigbay host' 60) { (Get-Content $log -TotalCount 1 -Encoding utf8) } else { 'NO LOG' }
$result.approved = Wait-Log 'scenes: scene-live: approval answer Approved' 90
if ($result.approved) {
    Start-Sleep -Seconds 2
    $mark = Log-Count
    $p = Start-Game
    $result.started = Wait-Log 'scenes: scene-live started for scenesgame.exe' 40 $mark
    Start-Sleep -Seconds 12
    $mark = Log-Count
    Stop-Process -Id $p.Id -Force
    $result.ended = Wait-Log 'scenes: scene-live ended' 60 $mark
}

# --- phase 2: Rigbay restarted while the game runs
if ($Restart -and $result.ended) {
    Stop-OurApp $app1
    Start-Sleep -Seconds 3
    $mark = Log-Count
    $app2 = Start-App 'enable:demo-plan-target;enable:scenes;page:scenes;wait:60000' 'restart.png' 90000
    $result.second_up = Wait-Log 'script: page:scenes' 60 $mark
    Start-Sleep -Seconds 3
    $mark = Log-Count
    $g = Start-Game
    $result.second_started = Wait-Log 'scenes: scene-live started for scenesgame.exe' 40 $mark
    Start-Sleep -Seconds 2
    $mark = Log-Count
    Stop-OurApp $app2                                  # Rigbay ends while the scene runs and the game goes on
    Start-Sleep -Seconds 3
    $app3 = Start-App 'enable:demo-plan-target;enable:scenes;page:scenes;wait:3000;expand-all;wait:1000;shot:g-adopted;wait:40000' 'restart2.png' 80000
    $result.interrupted_notice = Wait-Log 'were open when Rigbay ended' 60 $mark
    $result.adopted = Wait-Log 'scenes: scene-live: adopted run' 60 $mark
    Start-Sleep -Seconds 4
    $mark = Log-Count
    Stop-Process -Id $g.Id -Force
    $result.adopted_ended = Wait-Log 'scenes: scene-live ended' 60 $mark
    Start-Sleep -Seconds 3
    Stop-OurApp $app3
}

Start-Sleep -Seconds 5
Get-Process -Name 'scenesgame' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-OurApp $app1

# --- the report
$lines = @()
$lines += 'live check of the Game scenes module'
$lines += ($result.GetEnumerator() | ForEach-Object { '{0} = {1}' -f $_.Key, $_.Value })
$lines += ''
$lines += 'log lines about scenes and plans:'
if (Test-Path $log) { $lines += Get-Content $log -Encoding utf8 | Where-Object { $_ -match ' scenes |INFO  plans|WARN  plans| notice |notification sent|tray balloon|run [0-9a-f]{6,}|demo-plan-target plan step' } }
$lines += ''
$lines += 'shots:'
$lines += Get-ChildItem $shots -Filter *.png | ForEach-Object { '{0} {1} bytes' -f $_.FullName, $_.Length }
$lines | Set-Content -Path $report -Encoding utf8
$lines
