# Checks "Restart now" with two real, elevated Rigbay processes, in SCRATCH folders (tools\out\restart-test; never the real ProgramData folder, never GitHub):
#   process 1: installs demo-lighting from a local registry (file: source), then runs the "restart" step = the Restart now button's code
#   process 2: started by process 1 with --wait-pid <pid 1>; it waits for process 1 to end, takes the single-instance slot, applies the pending change
#              before the loader runs, and starts. It is stopped at the end (an elevated process needs tools\stop-elevated.ps1).
# Evidence: tools\shots\restart-test.txt (process ids, command line of process 2, the log lines, the folders).
# Refuses to run when another Rigbay.App is running (the single-instance slot would be shared with it).
$ErrorActionPreference = 'Stop'
$o = Join-Path $PSScriptRoot 'out'
$base = Join-Path $o 'restart-test'
$report = Join-Path $PSScriptRoot 'shots\restart-test.txt'
if (Get-Process Rigbay.App -ErrorAction SilentlyContinue) { throw 'Rigbay.App is running; stop it first (tools\stop-elevated.ps1)' }
& "$PSScriptRoot\remove-elevated.ps1" $base | Out-Null
New-Item -ItemType Directory -Force $base | Out-Null
& "$PSScriptRoot\make-local-registry.ps1" -Out (Join-Path $base 'registry') | Out-Null
$mods = Join-Path $base 'modules'
Copy-Item (Join-Path $base 'registry\modules') $mods -Recurse
$data = Join-Path $base 'data'
$reg = Join-Path $base 'registry\registry.json'
$lines = New-Object System.Collections.Generic.List[string]
function Say($s) { $lines.Add($s); Write-Host $s }

$p1 = & "$PSScriptRoot\run-elevated.ps1" -Data $data -Modules $mods --registry $reg --script 'page:modules;check;install:demo-lighting;wait:600;restart;wait:60000'
$p1pid = [int](($p1 | Select-Object -Last 1) -replace '[^0-9]', '')
Say "process 1 started: pid $p1pid"
$deadline = (Get-Date).AddSeconds(60)
$p2 = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $all = @(Get-Process Rigbay.App -ErrorAction SilentlyContinue)
    $p2 = $all | Where-Object { $_.Id -ne $p1pid } | Select-Object -First 1
    if ($p2 -and -not ($all | Where-Object { $_.Id -eq $p1pid })) { break }
}
if (-not $p2) { Say 'FAIL: no second process appeared'; $lines | Set-Content $report -Encoding utf8; & "$PSScriptRoot\stop-elevated.ps1"; exit 1 }
Say "process 2 started: pid $($p2.Id); process 1 (pid $p1pid) still running: $([bool](Get-Process -Id $p1pid -ErrorAction SilentlyContinue))"
Start-Sleep -Seconds 6
$log = Get-Content (Join-Path $data 'logs\rigbay.log')
Say '--- log lines about the restart'
$log | Select-String 'started with|restarting|quitting|window closed|install:|discovered|Rigbay host|second instance' | ForEach-Object { Say $_.Line }
# the scratch folders are admin-only now (a normal shell is refused: that is the point), so the evidence is the log of process 2
$present = [bool]($log | Select-String 'discovered .*demo-lighting=')
Say "demo-lighting found by the loader of process 2: $present"
& "$PSScriptRoot\stop-elevated.ps1" | Out-Null
Say 'process 2 stopped'
$lines | Set-Content $report -Encoding utf8
& "$PSScriptRoot\remove-elevated.ps1" $base | Out-Null
exit ([int](-not $present))
