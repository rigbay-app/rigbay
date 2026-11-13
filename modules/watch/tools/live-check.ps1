# Live check of the Watch module in YOUR OWN Rigbay instance, with the demo publisher (two made-up readings) as the thing being watched.
# It never touches a Rigbay that is already running: own data folder, own modules folder, --allow-multi (no pipe, no single-instance mutex), and no verb is sent
# to any other process (the verbs run inside this instance through the app's test script step "cmd:").
#
#   powershell -ExecutionPolicy Bypass -File modules\watch\tools\live-check.ps1            (needs tools\build.ps1 to have run)
#
# What happens, with the demo feed turned on at t = 0 (it publishes watch-demo.level = 40, then 100 from t = 10 s to 70 s, then 30):
#   rule "watch-demo.level at or above 90 for 20 s, clear at 80, quiet 1 min, check every 5 s" is in the saved settings, so
#   t ~ 15 s  the rule is Pending, t ~ 35 s it is Firing and ONE Warning notification is raised (a tray balloon: see the log lines "notice" and "tray balloon")
#   t ~ 75 s  the reading is back at 30: the episode ends; no second notification
# The script then shows the log lines of the module and the notification path, and leaves two pictures of the window (its own render, not the screen).
param(
    [string]$OutDir,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repo 'tools\out\watch-live' }
$modules = Join-Path $OutDir 'modules'
$data = Join-Path $OutDir 'data'
$shotBase = Join-Path $OutDir 'watch.png'
New-Item -ItemType Directory -Force $OutDir | Out-Null

if (-not $SkipBuild) { & "$PSScriptRoot\demo\build-demo.ps1" -OutDir $modules | Out-Host }
if (Test-Path $data) { & "$repo\tools\remove-elevated.ps1" $data | Out-Null }
Remove-Item (Join-Path $OutDir '*.png') -ErrorAction SilentlyContinue

# the saved rule (the same JSON the module writes itself)
$settings = Join-Path $data 'modules\watch\settings.json'
New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
@'
{ "version": 1, "data": { "trayItem": true, "rules": [
  { "id": "demo0001", "enabled": true, "metric": "watch-demo.level", "kind": "above", "threshold": 90, "clearAt": 80,
    "seconds": 20, "quietMinutes": 1, "intervalSeconds": 5, "conditionKind": "above", "conditionThreshold": 0 } ] } }
'@ | Set-Content -Path $settings -Encoding utf8

# the host is started as it is for a user: the window in use, so a Warning is a Windows notification AND a message in the window
$script = 'enable:watch-demo-feed;enable:watch;page:watch;wait:15000;shot:pending;wait:28000;shot:firing;cmd:watch status;wait:1500;cmd:watch pause 5;wait:1000;cmd:watch status;wait:1000;cmd:watch resume;wait:1000;cmd:watch status;wait:42000;cmd:watch status;wait:1500'
$code = & "$repo\tools\run-elevated.ps1" -Wait -Modules $modules -Data $data --allow-multi --theme dark --size 1240x900 --script $script --shot $shotBase --exit-after 1000 | Select-Object -Last 1
"exit code of the app: $code"

$log = Join-Path $data 'logs\rigbay.log'
if (-not (Test-Path $log)) { throw "no log at $log" }
"---- log lines (module, notification path, script)"
Get-Content $log -Encoding utf8 | Where-Object { $_ -match ' watch |watch-demo-feed|notice \[|tray balloon|toast|script: cmd|Warn|ERROR' } | ForEach-Object { $_ }
"---- pictures"
Get-ChildItem $OutDir -Filter *.png | ForEach-Object { $_.FullName }
