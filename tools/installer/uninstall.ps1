<#
.SYNOPSIS
  Removes Rigbay (the app) from this PC.

.DESCRIPTION
  Stops Rigbay, removes the scheduled task "Rigbay", the Start menu shortcut, the "Installed apps" entry and the program folder.
  Your modules (%ProgramData%\Rigbay\modules) and your settings and logs (%LOCALAPPDATA%\Rigbay) are KEPT, so installing
  again brings everything back. Use -RemoveData to delete those too.
  Run in an elevated Windows PowerShell (it offers to open one).

.PARAMETER Root
  Program folder. Default: the folder this script sits in when it was copied there by install.ps1, otherwise %ProgramFiles%\Rigbay.
.PARAMETER RemoveData
  Also delete %ProgramData%\Rigbay (modules and their install folders) and %LOCALAPPDATA%\Rigbay (settings, hotkeys, logs).
.PARAMETER DataRoot
  Machine-wide data folder. Default: %ProgramData%\Rigbay. Used by tests.
.PARAMETER UserDataDir
  Settings and logs folder. Default: %LOCALAPPDATA%\Rigbay. Used by tests.
.PARAMETER ShortcutDir
  Folder that holds the shortcut. Default: the all-users Start menu Programs folder. Used by tests.
.PARAMETER TaskName
  Name of the scheduled task. Default: Rigbay. Used by tests.
.PARAMETER NoPause
  When this script had to open an elevated window, close it at the end without waiting for Enter (for scripts).
.PARAMETER UninstallKeyRoot
  Registry key that holds the "Installed apps" entry. Default: the machine-wide Uninstall key. Used by tests.

.EXAMPLE
  .\uninstall.ps1 -WhatIf
.EXAMPLE
  .\uninstall.ps1 -RemoveData
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$Root,
    [switch]$RemoveData,
    [string]$DataRoot = (Join-Path $env:ProgramData 'Rigbay'),
    [string]$UserDataDir = (Join-Path $env:LOCALAPPDATA 'Rigbay'),
    [string]$ShortcutDir = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
    [string]$TaskName = 'Rigbay',
    [string]$UninstallKeyRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    [switch]$NoPause,
    [switch]$Relaunched
)

$ErrorActionPreference = 'Stop'
$exeName = 'Rigbay.App.exe'

function Say($text) { Write-Host $text }
# schtasks.exe writes to stderr when a task does not exist; with $ErrorActionPreference = 'Stop' that would become an exception
function Invoke-Schtasks([string[]]$Arguments) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & schtasks.exe @Arguments 2>&1 | ForEach-Object { "$_" }; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
    $global:LASTEXITCODE = 0
    return @{ Code = $code; Out = (($o -join ' ').Trim()) }
}
function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Stop-Uninstall($text) {
    Write-Host ''
    Write-Host "Rigbay was NOT fully removed. $text" -ForegroundColor Red
    if ($Relaunched -and -not $NoPause) { Read-Host 'Press Enter to close this window' | Out-Null }
    exit 1
}

if (-not $Root) {
    if (Test-Path (Join-Path $PSScriptRoot $exeName)) { $Root = $PSScriptRoot } else { $Root = Join-Path $env:ProgramFiles 'Rigbay' }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$dryRun = [bool]$WhatIfPreference
if (-not $isAdmin -and -not $dryRun) {
    Say 'Removing Rigbay needs an elevated PowerShell. Opening one now; Windows may ask you to confirm.'
    # run a copy from %TEMP%: this script may sit inside the folder it is about to delete
    $copy = Join-Path ([IO.Path]::GetTempPath()) ('rigbay-uninstall-{0}.ps1' -f $PID)
    Copy-Item $PSCommandPath $copy -Force
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $copy), '-Relaunched', '-Root', ('"{0}"' -f $Root))
    foreach ($key in $PSBoundParameters.Keys) {
        if ($key -in 'Relaunched', 'Root') { continue }
        $value = $PSBoundParameters[$key]
        if ($value -is [System.Management.Automation.SwitchParameter]) { if ($value.IsPresent) { $argList += "-$key" } }
        else { $argList += "-$key"; $argList += ('"{0}"' -f $value) }
    }
    try {
        $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait -PassThru
        Remove-Item $copy -ErrorAction SilentlyContinue
        exit $p.ExitCode
    }
    catch { Stop-Uninstall 'The elevated window could not be opened. Open PowerShell with "Run as administrator" and run this script again.' }
}
if (-not $isAdmin) { Say '(WhatIf: not elevated, so only reading; removing needs an elevated PowerShell.)' }

$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$DataRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
$UserDataDir = [IO.Path]::GetFullPath($UserDataDir).TrimEnd('\')
$rootExe = Join-Path $Root $exeName
$rootKnown = Test-Path $rootExe
Say "Program folder: $Root$(if (-not $rootKnown) { '  (not found, or not a Rigbay install)' })"

# --------------------------------------------------------------------------------------------------------------- stop the app
function Get-RunningFromRoot { @(Get-Process -Name 'Rigbay.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $rootExe) }) }
$running = Get-RunningFromRoot
if ($running.Count -gt 0) {
    if ($PSCmdlet.ShouldProcess("Rigbay.App (process $($running.Id -join ', '))", 'Quit, then stop if it does not end')) {
        Step 'Stopping Rigbay'
        $tmpOut = Join-Path ([IO.Path]::GetTempPath()) ('rigbay-quit-{0}.txt' -f $PID)
        try {
            # the app's own --uninstall-cleanup: quits the normal way (modules get their shutdown call) and removes the task of that name
            Start-Process -FilePath $rootExe -ArgumentList @('--uninstall-cleanup', '--task-name', $TaskName, '--out', ('"{0}"' -f $tmpOut)) -Wait -WindowStyle Hidden | Out-Null
            if (Test-Path $tmpOut) { Get-Content $tmpOut | ForEach-Object { Say "  $_" } }
        }
        catch { Say "  (could not ask it: $($_.Exception.Message))" }
        finally { Remove-Item $tmpOut -ErrorAction SilentlyContinue }
        $left = Get-RunningFromRoot
        if ($left.Count -gt 0) { Say 'It did not end, stopping it.'; $left | Stop-Process -Force; Start-Sleep -Milliseconds 800 }
        if ((Get-RunningFromRoot).Count -gt 0) { Stop-Uninstall 'Rigbay is still running and could not be stopped. Quit it from the tray icon and run this script again.' }
    }
}
else { Say 'Rigbay is not running.' }

# ------------------------------------------------------------------------------------------------------------------- the task
Step 'Start at logon'
$taskQuery = Invoke-Schtasks @('/query', '/tn', $TaskName)
$taskExists = ($taskQuery.Code -eq 0)
$global:LASTEXITCODE = 0
if ($taskExists) {
    if ($PSCmdlet.ShouldProcess("scheduled task '$TaskName'", 'Delete')) {
        $r = Invoke-Schtasks @('/delete', '/tn', $TaskName, '/f')
        if ($r.Code -ne 0) { Stop-Uninstall "The scheduled task '$TaskName' could not be deleted: $($r.Out)" }
        $global:LASTEXITCODE = 0
        Say "Scheduled task '$TaskName' removed."
    }
}
else { Say "No scheduled task named '$TaskName'." }

# ----------------------------------------------------------------------------------------------------- shortcut and app entry
Step 'Shortcut and app entry'
$lnk = Join-Path $ShortcutDir 'Rigbay.lnk'
if (Test-Path $lnk) { if ($PSCmdlet.ShouldProcess($lnk, 'Delete')) { Remove-Item $lnk -Force; Say "Removed $lnk" } } else { Say 'No Start menu shortcut.' }
$key = Join-Path $UninstallKeyRoot 'Rigbay'
if (Test-Path $key) { if ($PSCmdlet.ShouldProcess($key, 'Delete the "Installed apps" entry')) { Remove-Item $key -Recurse -Force; Say 'Removed the Installed apps entry.' } } else { Say 'No Installed apps entry.' }

# --------------------------------------------------------------------------------------------------------------- program folder
Step 'Program folder'
if (-not $rootKnown) { Say "Nothing to remove: '$Root' has no $exeName. (A folder that is not a Rigbay install is never deleted.)" }
else {
    if ($PSCmdlet.ShouldProcess($Root, 'Delete the program folder')) {
        Set-Location ([IO.Path]::GetTempPath())   # not inside the folder that is about to go
        Remove-Item $Root -Recurse -Force
        Say "Removed $Root"
    }
}

# ------------------------------------------------------------------------------------------------------------------ your data
Step 'Your data'
$modulesDir = Join-Path $DataRoot 'modules'
if ($RemoveData) {
    foreach ($dir in $DataRoot, $UserDataDir) {
        if (-not (Test-Path $dir)) { Say "$dir : not there."; continue }
        if ((Get-Item $dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { Say "$dir is a link, left alone."; continue }
        if ($PSCmdlet.ShouldProcess($dir, 'Delete (-RemoveData)')) { Remove-Item $dir -Recurse -Force; Say "Removed $dir" }
    }
}
else {
    Say "Kept: $modulesDir (modules) and $UserDataDir (settings, hotkeys, logs)."
    Say 'Install Rigbay again to get them back, or run this script with -RemoveData to delete them.'
}

Step 'Done'
if ($dryRun) { Say 'WhatIf: nothing was changed.' } else { Say 'Rigbay is removed.' }
if ($Relaunched -and -not $NoPause) { Read-Host 'Press Enter to close this window' | Out-Null }
exit 0
