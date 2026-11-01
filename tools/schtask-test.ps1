# "Start with Windows": creates the scheduled task through the app's own code (Settings switch path), shows its XML, starts it with
# schtasks /run, checks the process, the tray icon line in the log and that the instance answers over its pipe, then removes the
# task through the app's own code and checks it is gone. A real logoff/logon is NOT performed.
# Needs an elevated PowerShell; relaunches itself elevated if it is not. Refuses to run if a task named Rigbay already exists.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$report = Join-Path $PSScriptRoot 'out\schtask-report.txt'
New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
if (-not $isAdmin) {
    Remove-Item $report -ErrorAction SilentlyContinue
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    Get-Content $report -Encoding utf8
    return
}
$ErrorActionPreference = 'Continue'
$out = New-Object System.Collections.Generic.List[string]
function Say($s) { $out.Add([string]$s) }
function Finish { $out | Set-Content $report -Encoding utf8 }

schtasks /query /tn Rigbay 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Say "a scheduled task named Rigbay already exists; not touching it"; Finish; return }

$exe = Join-Path $PSScriptRoot 'out\app\Rigbay.App.exe'
$realData = Join-Path $env:LOCALAPPDATA 'Rigbay'
$dataExisted = Test-Path $realData
$tmp = Join-Path $PSScriptRoot 'out\data-schtask'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
$empty = Join-Path $PSScriptRoot 'out\modules-empty'
New-Item -ItemType Directory -Force $empty | Out-Null

# 1. create through the app (a throwaway instance with its own data folder does the work, as the Settings switch would)
Say "create: Rigbay.App.exe --script startup:on   (same code as the Settings switch)"
$p = Start-Process $exe -ArgumentList '--data', "`"$tmp`"", '--modules', "`"$empty`"", '--allow-multi', '--script', 'startup:on', '--exit-after', '300' -Wait -PassThru
Say "  app exit code $($p.ExitCode)"
schtasks /query /tn Rigbay 2>$null | Out-Null
Say "task exists: $($LASTEXITCODE -eq 0)"
$xml = (schtasks /query /tn Rigbay /xml) -join "`n"
foreach ($pat in 'LogonTrigger', '<Delay>', 'LogonType', 'RunLevel', 'DisallowStartIfOnBatteries', 'StopIfGoingOnBatteries', 'MultipleInstancesPolicy', '<Command>', '<Arguments>') {
    ($xml -split "`n") | Where-Object { $_ -match $pat } | ForEach-Object { Say ("  " + $_.Trim()) }
}

# 2. run it like the logon trigger would
Say "run: schtasks /run /tn Rigbay"
Say ((schtasks /run /tn Rigbay) -join ' ')
$proc = $null
for ($i = 0; $i -lt 40 -and -not $proc; $i++) { Start-Sleep -Milliseconds 500; $proc = Get-Process Rigbay.App -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($proc) {
    Start-Sleep -Seconds 4
    $proc.Refresh()
    Say ("process: pid {0}, session {1} (the interactive desktop is session {2}), responding={3}" -f $proc.Id, $proc.SessionId, (Get-Process -Id $PID).SessionId, $proc.Responding)
    $log = Join-Path $realData 'logs\rigbay.log'
    if (Test-Path $log) { Say "log lines of that start:"; Get-Content $log -Tail 40 -Encoding utf8 | Select-String 'starting; args|tray icon|toast|discovered|modules in' | ForEach-Object { Say ("  " + $_.Line.Trim()) } }
    # does the running instance answer? (the command line is forwarded to it over its pipe)
    $f = Join-Path $PSScriptRoot 'out\schtask-list.txt'
    Remove-Item $f -ErrorAction SilentlyContinue
    Start-Process $exe -ArgumentList '--out', "`"$f`"", 'list' -Wait | Out-Null
    Say ("forwarded 'list' to the running instance: " + ((Get-Content $f -ErrorAction SilentlyContinue) -join ' | '))
    Say ("process still running after the check: " + [bool](Get-Process -Id $proc.Id -ErrorAction SilentlyContinue))
} else { Say "process: Rigbay.App never appeared" }
Get-Process Rigbay.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 3. remove through the app
Say "delete: Rigbay.App.exe --script startup:off"
$p = Start-Process $exe -ArgumentList '--data', "`"$tmp`"", '--modules', "`"$empty`"", '--allow-multi', '--script', 'startup:off', '--exit-after', '300' -Wait -PassThru
schtasks /query /tn Rigbay 2>$null | Out-Null
$gone = ($LASTEXITCODE -ne 0)
Say "  app exit code $($p.ExitCode); task gone: $gone"
if (-not $gone) { Say "  cleanup with schtasks /delete"; schtasks /delete /tn Rigbay /f | Out-Null }
Say ("no Rigbay.App process left: " + (-not (Get-Process Rigbay.App -ErrorAction SilentlyContinue)))
if (-not $dataExisted -and (Test-Path $realData)) { Remove-Item $realData -Recurse -Force; Say "removed the %LOCALAPPDATA%\Rigbay folder this test created" }
Finish
