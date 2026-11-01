# Stops every Rigbay.Host process. An elevated process cannot be stopped from a normal shell,
# so this relaunches itself elevated when needed.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    $left = Get-Process Rigbay.Host -ErrorAction SilentlyContinue
    if ($left) { "still running: " + ($left.Id -join ', ') } else { "no Rigbay.Host running" }
    return
}
Get-Process Rigbay.Host -ErrorAction SilentlyContinue | ForEach-Object { "stopping $($_.Id)"; $_.Kill() }
