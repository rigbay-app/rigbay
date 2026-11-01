# Part (c): logon task with highest privileges. Creates RigbaySpike, starts it with schtasks /run, checks the process
# and its window, then deletes the task. Needs an elevated PowerShell; relaunches itself elevated if it is not.
# A real logoff/logon is not performed.
param([ValidateSet('fd', 'sc')][string]$Variant = 'fd')
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$report = Join-Path $PSScriptRoot 'out\schtask-report.txt'
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Variant', $Variant
    Get-Content $report
    return
}
$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot "out\host-$Variant\Rigbay.Host.exe"
$log = Join-Path $PSScriptRoot 'out\schtask-host.log'
Remove-Item $log, $report -ErrorAction SilentlyContinue
$out = New-Object System.Collections.Generic.List[string]
function Say($s) { $out.Add($s) }

$tr = "`"$exe`" --log `"$log`" --selftest --exit-after 9000"
Say "create:  schtasks /create /tn RigbaySpike /sc onlogon /rl highest /tr <exe + args> /f"
Say ((schtasks /create /tn RigbaySpike /sc onlogon /rl highest /tr $tr /f) -join ' ')
Say "query:   " + ((schtasks /query /tn RigbaySpike /fo list /v | Select-String 'Task Name|Run As User|Logon Mode|Schedule Type|Scheduled Task State|Status' | % { $_.Line.Trim() }) -join ' | ')
$xml = schtasks /query /tn RigbaySpike /xml
Say ("RunLevel in task XML: " + (($xml | Select-String 'RunLevel|LogonTrigger|LogonType') -join ' ').Trim())
Say "run:     schtasks /run /tn RigbaySpike"
Say ((schtasks /run /tn RigbaySpike) -join ' ')

$p = $null
for ($i = 0; $i -lt 30 -and -not $p; $i++) { Start-Sleep -Milliseconds 500; $p = Get-Process Rigbay.Host -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($p) {
    Start-Sleep -Seconds 3
    $p.Refresh()
    Say ("process: pid {0}, session {1}, MainWindowHandle 0x{2:X}, MainWindowTitle '{3}', responding={4}" -f $p.Id, $p.SessionId, $p.MainWindowHandle.ToInt64(), $p.MainWindowTitle, $p.Responding)
    Start-Sleep -Seconds 8
    Say ("process still running after 11 s: " + [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue))
} else { Say "process: Rigbay.Host never appeared" }
if (Test-Path $log) { Say "host log (elevation, runtime):"; Get-Content $log | Select-String 'SELFTEST (PASS|FAIL) (elevated|runtime source)' | % { Say ("  " + $_.Line) } } else { Say "host log: missing" }
Get-Process Rigbay.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Say "delete:  schtasks /delete /tn RigbaySpike /f"
Say ((schtasks /delete /tn RigbaySpike /f) -join ' ')
Say ("task gone: " + [bool](-not (schtasks /query /tn RigbaySpike 2>$null)))
$out | Set-Content $report -Encoding utf8
