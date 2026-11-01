# Stops every Rigbay.App process. An elevated process cannot be stopped from a normal shell, so this relaunches itself elevated.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    $left = Get-Process Rigbay.App -ErrorAction SilentlyContinue
    if ($left) { "still running: " + ($left.Id -join ', ') } else { "no Rigbay.App running" }
    return
}
Get-Process Rigbay.App -ErrorAction SilentlyContinue | ForEach-Object { "stopping $($_.Id)"; $_.Kill() }
