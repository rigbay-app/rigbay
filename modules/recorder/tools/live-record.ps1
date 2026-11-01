# Live check of the recorder with the PredatorSense module in the real Rigbay app (task M2). READS ONLY: the sensors of PredatorSense are
# approved reads; nothing here turns a fan, power limit or any other setting. XTU is not loaded.
#
#   .\live-record.ps1 record [-Seconds 20]       records for N seconds through the running app's verbs and checks the CSV and the meta
#   .\live-record.ps1 shot <name> [-Theme dark|light] [-Page recorder|predatorsense] [-Recording] [-Lang vi]
#                                                renders the window itself (PrintWindow of the app's own HWND, never the screen) to docs\recorder\live\<name>.png
#
# Prerequisites: tools\build.ps1 -SkipDemo (the app in tools\out\app), and the two modules built in Release and copied to tools\out\modules-live\<id>
# (this script copies them: -Rebuild). The app runs elevated (the manifest asks for it; UAC auto-elevates for this account) with its own data folder,
# tools\out\data-live, so the real %LOCALAPPDATA%\Rigbay is not touched. Every run stops its own app process at the end.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0, Mandatory)][ValidateSet('record', 'shot')][string]$Mode,
    [Parameter(Position = 1)][string]$Name = 'shot',
    [int]$Seconds = 20,
    [ValidateSet('dark', 'light')][string]$Theme = 'dark',
    [string]$Page = 'recorder',
    [string]$Lang = 'en',
    [switch]$Recording,
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$exe = Join-Path $repo 'tools\out\app\Rigbay.App.exe'
$mods = Join-Path $repo 'tools\out\modules-live'
$data = Join-Path $repo 'tools\out\data-live'
$docs = Join-Path $repo 'docs\recorder\live'
$logs = Join-Path $repo 'tools\out\live-logs'
if (-not (Test-Path $exe)) { throw "Not built: $exe (run tools\build.ps1 -SkipDemo)" }
New-Item -ItemType Directory -Force $docs | Out-Null

if ($Rebuild -or -not (Test-Path $mods)) {
    foreach ($m in @(@('recorder', 'modules\recorder\src\Rigbay.Recorder'), @('predatorsense', 'modules\predatorsense\src\Rigbay.PredatorSense'))) {
        & dotnet build (Join-Path $repo $m[1]) -c Release --nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: $($m[0])" }
        $to = Join-Path $mods $m[0]
        if (Test-Path $to) { Remove-Item $to -Recurse -Force }
        New-Item -ItemType Directory -Force $to | Out-Null
        Copy-Item (Join-Path $repo "$($m[1])\bin\Release\net8.0\*") $to -Recurse
    }
}

function Stop-App {
    if (Get-Process Rigbay.App -ErrorAction SilentlyContinue) {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $repo 'tools\stop-elevated.ps1')`""
    }
}

function Start-App([string[]]$more) {
    $all = @('--data', $data, '--modules', $mods) + $more
    $arg = $all | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    Start-Process -FilePath $exe -Verb RunAs -PassThru -WorkingDirectory (Split-Path $exe) -ArgumentList $arg
}

# a verb through the running instance (the exe forwards it over the pipe) -> exit code and text
function Verb([string[]]$words) {
    $f = Join-Path $repo 'tools\out\live-verb.txt'
    Remove-Item $f -ErrorAction SilentlyContinue
    $all = @('--data', "`"$data`"", '--modules', "`"$mods`"", '--out', "`"$f`"") + $words
    $p = Start-Process $exe -ArgumentList $all -Wait -PassThru -Verb RunAs
    $text = if (Test-Path $f) { (Get-Content $f -Raw -Encoding utf8).Trim() } else { '' }
    [pscustomobject]@{ Exit = $p.ExitCode; Text = $text; At = Get-Date }
}

function Wait-ForLog([string]$text, [int]$timeoutSec = 90) {
    $log = Join-Path $data 'logs\rigbay.log'
    $end = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $end) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern ([regex]::Escape($text)) -Quiet)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

Stop-App
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force $data | Out-Null

if ($Mode -eq 'record') {
    $before = @(Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Rigbay\logs') -Filter 'rigbay-log-*.csv' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $shot = Join-Path $docs "$Name.png"
    Remove-Item $shot -ErrorAction SilentlyContinue
    $hold = ($Seconds + 40) * 1000
    $app = Start-App @('--theme', $Theme, '--shot', $shot, '--exit-after', '500', '--script', "enable:predatorsense;enable:recorder;page:predatorsense;wait:$hold")
    if (-not (Wait-ForLog 'script: wait:')) { throw 'the app did not reach the end of its enable steps' }
    Start-Sleep -Seconds 4   # one PredatorSense poll after the page opened

    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("modules: " + (Verb @('list')).Text)
    $report.Add("--- status before"); $report.Add((Verb @('recorder', 'status')).Text)
    $start = Verb @('recorder', 'start')
    $report.Add("--- start (exit $($start.Exit)) at $($start.At.ToString('HH:mm:ss.fff'))"); $report.Add($start.Text)
    Start-Sleep -Seconds ([int]($Seconds / 2))
    $mid = Verb @('predatorsense', 'status')
    $report.Add("--- predatorsense status at $($mid.At.ToString('HH:mm:ss.fff')) (exit $($mid.Exit))"); $report.Add($mid.Text)
    $running = Verb @('recorder', 'status')
    $report.Add("--- recorder status while recording"); $report.Add($running.Text)
    $left = $Seconds - [int]((Get-Date) - $start.At).TotalSeconds
    if ($left -gt 0) { Start-Sleep -Seconds $left }
    $stop = Verb @('recorder', 'stop')
    $report.Add("--- stop (exit $($stop.Exit)) at $($stop.At.ToString('HH:mm:ss.fff'))"); $report.Add($stop.Text)
    $report.Add("--- status after"); $report.Add((Verb @('recorder', 'status')).Text)
    $report | Set-Content (Join-Path $docs "$Name-verbs.txt") -Encoding utf8
    $report

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process Rigbay.App -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Stop-App

    $new = Get-ChildItem (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Rigbay\logs') -Filter 'rigbay-log-*.csv' | Where-Object { $before -notcontains $_.FullName }
    "new recordings: $(@($new).Count)"
    foreach ($f in $new) {
        Copy-Item $f.FullName (Join-Path $docs "$Name.csv") -Force
        Copy-Item ([IO.Path]::ChangeExtension($f.FullName, $null) + 'meta.json') (Join-Path $docs "$Name.meta.json") -Force
        "copied $($f.Name) -> docs\recorder\live\$Name.csv / .meta.json"
    }
    Copy-Item (Join-Path $data 'logs\rigbay.log') (Join-Path $docs "$Name-app.log") -Force
    return
}

# shots: the window itself, never the screen
$logs = Join-Path $logs $Name   # one folder per shot: the recordings of a shot run stay out of Documents\Rigbay\logs
New-Item -ItemType Directory -Force $logs | Out-Null
$shot = Join-Path $docs "$Name.png"
Remove-Item $shot -ErrorAction SilentlyContinue
$script = "enable:predatorsense;enable:recorder;page:$Page;wait:3500"
if ($Recording) {
    # the folder is a module setting: seed it in the data folder (a script cannot press Apply on the folder row)
    $settings = Join-Path $data 'modules\recorder'
    New-Item -ItemType Directory -Force $settings | Out-Null
    $json = (@{ version = 1; data = @{ folder = $logs } } | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText((Join-Path $settings 'settings.json'), $json, (New-Object Text.UTF8Encoding($false)))
    $script = "enable:predatorsense;enable:recorder;page:recorder;wait:800;cmd:recorder start;wait:5500;page:$Page;wait:600"
}
$more = @('--theme', $Theme, '--lang', $Lang, '--size', '1240x2100', '--shot', $shot, '--exit-after', '800', '--script', $script)
$app = Start-App $more
$deadline = (Get-Date).AddSeconds(120)
Start-Sleep -Seconds 2
while ((Get-Process Rigbay.App -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Stop-App
"{0}: {1}" -f $Name, (Test-Path $shot)
if ($Recording) {
    $csv = Get-ChildItem $logs -Filter 'rigbay-log-*.csv' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($csv) {
        Copy-Item $csv.FullName (Join-Path $docs "$Name.csv") -Force
        Copy-Item ([IO.Path]::ChangeExtension($csv.FullName, $null) + 'meta.json') (Join-Path $docs "$Name.meta.json") -Force
    }
    Select-String -Path (Join-Path $data 'logs\rigbay.log') -Pattern 'shot \(' | ForEach-Object { $_.Line } | Set-Content (Join-Path $docs "$Name-shot-time.txt") -Encoding utf8
}
