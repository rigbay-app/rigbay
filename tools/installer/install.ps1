<#
.SYNOPSIS
  Installs Rigbay (the app, not modules) on this PC.

.DESCRIPTION
  Run this in an elevated Windows PowerShell (it offers to open one if you did not).
  What it does, and nothing else:
    1. checks Windows 11, x64, the .NET 10 runtime and the Windows App Runtime 1.8 (Rigbay is framework-dependent);
       if one is missing it says what and how to install it (winget) and asks before running winget;
    2. copies the app to %ProgramFiles%\Rigbay (stops a running Rigbay first; an upgrade keeps settings and modules);
    3. creates %ProgramData%\Rigbay\modules, writable only by Administrators and SYSTEM;
    4. adds a Start menu shortcut, a scheduled task "Rigbay" that starts Rigbay in the tray at logon (15 s delay,
       highest privileges, no prompt), and an entry in Settings > Apps > Installed apps.
  Nothing is signed: Windows SmartScreen may warn when you first run the downloaded files.

.PARAMETER Root
  Program folder. Default: %ProgramFiles%\Rigbay.
.PARAMETER NoTask
  Do not create the "start at logon" task (you can switch it on later in Rigbay > Settings).
.PARAMETER NoShortcut
  Do not create the Start menu shortcut.
.PARAMETER InstallPrerequisites
  Run winget for missing prerequisites without asking.
.PARAMETER DataRoot
  Machine-wide data folder (modules live in <DataRoot>\modules). Default: %ProgramData%\Rigbay. Used by tests.
.PARAMETER ShortcutDir
  Folder for the shortcut. Default: the all-users Start menu Programs folder. Used by tests.
.PARAMETER TaskName
  Name of the scheduled task. Default: Rigbay. Used by tests.
.PARAMETER UninstallKeyRoot
  Registry key that holds the "Installed apps" entry. Default: the machine-wide Uninstall key. Used by tests.
.PARAMETER NoUninstallEntry
  Do not write the "Installed apps" entry.
.PARAMETER SkipPrerequisiteCheck
  Do not check the prerequisites (tests on a machine that has them anyway; Rigbay will not start without them).

.EXAMPLE
  .\install.ps1
.EXAMPLE
  .\install.ps1 -WhatIf          # shows every step, changes nothing
.EXAMPLE
  .\install.ps1 -Root D:\Apps\Rigbay -NoTask
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$Root = (Join-Path $env:ProgramFiles 'Rigbay'),
    [switch]$NoTask,
    [switch]$NoShortcut,
    [switch]$InstallPrerequisites,
    [string]$DataRoot = (Join-Path $env:ProgramData 'Rigbay'),
    [string]$ShortcutDir = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
    [string]$TaskName = 'Rigbay',
    [string]$UninstallKeyRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    [switch]$NoUninstallEntry,
    [switch]$SkipPrerequisiteCheck,
    [switch]$Relaunched
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$appSource = Join-Path $here 'app'
$exeName = 'Rigbay.App.exe'

function Say($text) { Write-Host $text }
function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Stop-Install($text) {
    Write-Host ''
    Write-Host "Rigbay was NOT installed. $text" -ForegroundColor Red
    if ($Relaunched) { Read-Host 'Press Enter to close this window' | Out-Null }
    exit 1
}

# ---------------------------------------------------------------------------------------------------------------- elevation
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$dryRun = [bool]$WhatIfPreference
if (-not $isAdmin -and -not $dryRun) {
    Say 'Rigbay installs into Program Files and runs as administrator, so this script needs an elevated PowerShell.'
    Say 'Opening one now. Windows may ask you to confirm; the install continues in that window.'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath), '-Relaunched')
    foreach ($key in $PSBoundParameters.Keys) {
        if ($key -eq 'Relaunched') { continue }
        $value = $PSBoundParameters[$key]
        if ($value -is [System.Management.Automation.SwitchParameter]) { if ($value.IsPresent) { $argList += "-$key" } }
        else { $argList += "-$key"; $argList += ('"{0}"' -f $value) }
    }
    try {
        $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    }
    catch { Stop-Install 'The elevated window could not be opened (the confirmation was refused?). Open PowerShell with "Run as administrator" and run this script again.' }
}
if (-not $isAdmin) { Say '(WhatIf: not elevated, so only reading; an install needs an elevated PowerShell.)' }

if (-not (Test-Path (Join-Path $appSource $exeName))) {
    Stop-Install "Cannot find app\$exeName next to this script. Extract the whole zip first, then run install.ps1 from the extracted folder."
}
$version = '?'
try { $version = (Get-Item (Join-Path $appSource $exeName)).VersionInfo.ProductVersion } catch { }
Say "Rigbay $version"

# ------------------------------------------------------------------------------------------------------------ prerequisites
$missing = @()   # items: @{ What; Id; Why }
if (-not $SkipPrerequisiteCheck) {
    Step 'Checking this PC'
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 22000) { Stop-Install "This is Windows build $build. Rigbay needs Windows 11 (build 22000 or newer)." }
    Say "Windows 11, build $build: ok"

    $arch = $null
    try { $arch = [string][System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { }
    if (-not $arch) { $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE } }
    if ($arch -notmatch '^(X64|AMD64)$') { Stop-Install "This PC is $arch. Rigbay 0.1 is built for x64 only." }
    Say 'x64: ok'

    # .NET 10 runtime (Microsoft.NETCore.App 10.x; the Desktop runtime contains it too)
    $dotnet = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    $netOk = $false
    if ($dotnet) {
        $runtimes = & $dotnet --list-runtimes 2>$null
        $netOk = [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })
    }
    if ($netOk) { Say '.NET 10 runtime: ok' }
    else { $missing += @{ What = '.NET 10 runtime (Microsoft.NETCore.App 10.x)'; Id = 'Microsoft.DotNet.Runtime.10'; Why = 'Rigbay is built for .NET 10 and does not bring its own copy.' } }

    # Windows App Runtime 1.8 (framework package, x64)
    if ($PSVersionTable.PSEdition -eq 'Core') { try { Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue } catch { } }
    $pkgs = @()
    try { if ($isAdmin) { $pkgs = @(Get-AppxPackage -AllUsers -Name 'Microsoft.WindowsAppRuntime.1.8*') } else { $pkgs = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8*') } } catch { }
    $warOk = [bool]($pkgs | Where-Object { $_.Architecture -eq 'X64' -or "$($_.Architecture)" -eq '9' })
    if ($warOk) { Say 'Windows App Runtime 1.8: ok' }
    else { $missing += @{ What = 'Windows App Runtime 1.8 (x64)'; Id = 'Microsoft.WindowsAppRuntime.1.8'; Why = 'Rigbay draws its window with WinUI 3, which comes from this runtime.' } }

    if ($missing.Count -gt 0) {
        Step 'Missing prerequisites'
        foreach ($m in $missing) {
            Say ("- {0}: {1}" -f $m.What, $m.Why)
            Say ("    install with:  winget install --id {0} -e --source winget" -f $m.Id)
        }
        $winget = Get-Command winget -ErrorAction SilentlyContinue
        $go = $false
        if (-not $winget) {
            Stop-Install 'winget was not found. Install the items above yourself (the winget lines show the package names), then run this script again.'
        }
        if ($dryRun) {
            foreach ($m in $missing) { Say ("What if: would run  winget install --id {0} -e --source winget" -f $m.Id) }
        }
        else {
            if ($InstallPrerequisites) { $go = $true }
            else {
                $answer = ''
                try { $answer = Read-Host 'Run winget for the items above now? This installs software on this PC. [y/N]' } catch { }
                $go = ($answer -match '^(y|yes)$')
            }
            if (-not $go) { Stop-Install 'Install the items above (or run this script with -InstallPrerequisites) and try again.' }
            foreach ($m in $missing) {
                Say "Installing $($m.What) ..."
                & $winget.Source install --id $m.Id -e --source winget --accept-package-agreements --accept-source-agreements
                if ($LASTEXITCODE -ne 0) { Stop-Install "winget could not install $($m.Id) (exit code $LASTEXITCODE). Install it yourself and run this script again." }
            }
            Say 'Prerequisites installed.'
        }
    }
}

# ------------------------------------------------------------------------------------------------------------ safety of -Root
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$DataRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
$rootExe = Join-Path $Root $exeName
if ((Test-Path $Root) -and -not (Test-Path $rootExe)) {
    if (@(Get-ChildItem $Root -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        Stop-Install "The folder '$Root' exists, is not empty and is not a Rigbay install ($exeName is not in it). Pick another folder with -Root; this script never changes a folder it does not recognise."
    }
}
$upgrade = Test-Path $rootExe
Step $(if ($upgrade) { "Upgrading $Root" } else { "Installing to $Root" })

# --------------------------------------------------------------------------------------------------------- stop a running one
# Only a process that runs from -Root is touched (another copy elsewhere, for example a test copy, is left alone).
function Get-RunningFromRoot {
    @(Get-Process -Name 'Rigbay.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $rootExe) })
}
$running = Get-RunningFromRoot
if ($running.Count -gt 0) {
    if ($PSCmdlet.ShouldProcess("Rigbay.App (process $($running.Id -join ', '))", 'Quit, then stop if it does not end')) {
        Say 'Rigbay is running: asking it to quit ...'
        $tmpOut = Join-Path ([IO.Path]::GetTempPath()) ('rigbay-quit-{0}.txt' -f $PID)
        try {
            Start-Process -FilePath $rootExe -ArgumentList @('--uninstall-cleanup', '--keep-task', '--out', ('"{0}"' -f $tmpOut)) -Wait -WindowStyle Hidden | Out-Null
            if (Test-Path $tmpOut) { Get-Content $tmpOut | ForEach-Object { Say "  $_" } }
        }
        catch { Say "  (could not ask it: $($_.Exception.Message))" }
        finally { Remove-Item $tmpOut -ErrorAction SilentlyContinue }
        $left = Get-RunningFromRoot
        if ($left.Count -gt 0) { Say 'It did not end, stopping it.'; $left | Stop-Process -Force; Start-Sleep -Milliseconds 800 }
        if ((Get-RunningFromRoot).Count -gt 0) { Stop-Install 'Rigbay is still running and could not be stopped. Quit it from the tray icon and run this script again.' }
    }
}

# ----------------------------------------------------------------------------------------------------------------- copy files
if ($PSCmdlet.ShouldProcess($Root, "Copy the app ($version) here, replacing the files of the earlier version")) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    # /MIR makes the folder an exact copy of app\, so files of an older version that no longer exist are removed. The folder holds
    # program files only: settings are in %LOCALAPPDATA%\Rigbay and modules in the data folder, neither is touched here.
    $null = & robocopy $appSource $Root /MIR /R:3 /W:1 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { Stop-Install "Copying the files failed (robocopy exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
    $uninstallSource = Join-Path $here 'uninstall.ps1'
    if (Test-Path $uninstallSource) { Copy-Item $uninstallSource (Join-Path $Root 'uninstall.ps1') -Force }
    foreach ($f in 'LICENSE', 'README.txt') { $s = Join-Path $here $f; if (Test-Path $s) { Copy-Item $s (Join-Path $Root $f) -Force } }
    Say "Copied to $Root"
}

# ------------------------------------------------------------------------------------------------------------ the data folder
function Set-AdminOnly([string]$path) {
    # Same rule as the app's own AdminFolderSecurity: protected DACL, FullControl for Administrators and SYSTEM only, inherited below.
    if ((Test-Path $path) -and ((Get-Item $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "'$path' is a link, not a folder" }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $admins = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $system = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $acl = Get-Acl $path
    $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not $owner.Equals($admins) -and -not $owner.Equals($system)) { $acl.SetOwner($admins); Set-Acl $path $acl; $acl = Get-Acl $path }
    $flags = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $rules = @($acl.Access | Where-Object { -not $_.IsInherited })
    $exact = $acl.AreAccessRulesProtected -and ($acl.Access.Count -eq 2) -and
        ($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -eq 'FullControl' -and $_.InheritanceFlags -eq $flags -and -not $_.IsInherited }).Count -eq 2 -and
        ($acl.Access | Where-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Equals($admins) }).Count -eq 1 -and
        ($acl.Access | Where-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Equals($system) }).Count -eq 1
    if ($exact) { return }
    $new = New-Object System.Security.AccessControl.DirectorySecurity
    $new.SetAccessRuleProtection($true, $false)
    foreach ($sid in $admins, $system) {
        $new.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule ($sid, [System.Security.AccessControl.FileSystemRights]::FullControl, $flags, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl $path $new
}
$modulesDir = Join-Path $DataRoot 'modules'
if ($PSCmdlet.ShouldProcess($modulesDir, 'Create (or keep) the modules folder; writable by Administrators and SYSTEM only')) {
    Step 'Modules folder'
    Set-AdminOnly $DataRoot
    Set-AdminOnly $modulesDir
    Say "$modulesDir (Administrators and SYSTEM only)"
    # modules shipped next to this script (the zip's empty "modules" folder is for this): copied only if that id is not installed yet.
    # They arrive turned off; Rigbay asks before it runs one.
    $bundled = Join-Path $here 'modules'
    if (Test-Path $bundled) {
        foreach ($m in Get-ChildItem $bundled -Directory -ErrorAction SilentlyContinue) {
            if (-not (Test-Path (Join-Path $m.FullName 'module.json'))) { continue }
            $target = Join-Path $modulesDir $m.Name
            if (Test-Path $target) { Say "Module $($m.Name) is already installed, left as it is."; continue }
            Copy-Item $m.FullName $target -Recurse
            Say "Module $($m.Name) copied (it is off until you turn it on in Rigbay)."
        }
    }
}

# ---------------------------------------------------------------------------------------------------------------- shortcut
$lnk = Join-Path $ShortcutDir 'Rigbay.lnk'
if (-not $NoShortcut) {
    if ($PSCmdlet.ShouldProcess($lnk, 'Create the Start menu shortcut')) {
        New-Item -ItemType Directory -Force -Path $ShortcutDir | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath = $rootExe
        $sc.WorkingDirectory = $Root
        $sc.IconLocation = "$rootExe,0"
        $sc.Description = 'Rigbay: modules for your own machine'
        $sc.Save()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        Say "Start menu shortcut: $lnk"
    }
}

# --------------------------------------------------------------------------------------------------------- start at logon task
function Get-TaskXml([string]$user, [string]$exe) {
    # Same task the app's Settings switch creates (StartupTask.cs): logon of this user, 15 s delay, highest privileges, --tray,
    # and "start on battery" allowed (schtasks /create options cannot say that).
    $e = { param($s) [System.Security.SecurityElement]::Escape($s) }
    $dir = Split-Path $exe -Parent
    @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts Rigbay in the tray when $(& $e $user) logs on, elevated, without a prompt.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>$(& $e $user)</UserId><Delay>PT15S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$(& $e $user)</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$(& $e $exe)</Command><Arguments>--tray</Arguments><WorkingDirectory>$(& $e $dir)</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
}
if (-not $NoTask) {
    # the user who signs in: when an administrator account other than the signed-in one confirmed the elevation, the task must still be for the signed-in user
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    try { $console = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName; if ($console) { $user = $console } } catch { }
    if ($PSCmdlet.ShouldProcess("scheduled task '$TaskName' for $user", 'Create (start Rigbay in the tray at logon, 15 s delay, highest privileges)')) {
        Step 'Start at logon'
        $xmlFile = Join-Path ([IO.Path]::GetTempPath()) ('rigbay-task-{0}.xml' -f $PID)
        try {
            [IO.File]::WriteAllText($xmlFile, (Get-TaskXml $user $rootExe), [Text.Encoding]::Unicode)
            $out = & schtasks.exe /create /tn $TaskName /xml $xmlFile /f 2>&1
            if ($LASTEXITCODE -ne 0) { Stop-Install "The scheduled task could not be created: $out`nRun again with -NoTask to install without it." }
            $global:LASTEXITCODE = 0
            Say "Scheduled task '$TaskName' created for $user."
        }
        finally { Remove-Item $xmlFile -ErrorAction SilentlyContinue }
    }
    elseif ($dryRun) { Say 'Task XML that would be registered:'; Say (Get-TaskXml $user $rootExe) }
}

# -------------------------------------------------------------------------------------------------------- uninstall entry
if (-not $NoUninstallEntry) {
    $key = Join-Path $UninstallKeyRoot 'Rigbay'
    if ($PSCmdlet.ShouldProcess($key, 'Write the "Installed apps" entry')) {
        $kb = 0
        try { $kb = [int]((Get-ChildItem $Root -Recurse -File | Measure-Object Length -Sum).Sum / 1KB) } catch { }
        New-Item -Path $key -Force | Out-Null
        $uninstallCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $Root 'uninstall.ps1')
        $values = [ordered]@{
            DisplayName = 'Rigbay'; DisplayVersion = $version; Publisher = 'Rigbay contributors'; InstallLocation = $Root
            DisplayIcon = $rootExe; UninstallString = $uninstallCmd; URLInfoAbout = 'https://github.com/rigbay-app/rigbay'
        }
        foreach ($k in $values.Keys) { New-ItemProperty -Path $key -Name $k -Value $values[$k] -PropertyType String -Force | Out-Null }
        foreach ($k in 'NoModify', 'NoRepair') { New-ItemProperty -Path $key -Name $k -Value 1 -PropertyType DWord -Force | Out-Null }
        New-ItemProperty -Path $key -Name EstimatedSize -Value $kb -PropertyType DWord -Force | Out-Null
        Say 'Entry added to Settings > Apps > Installed apps.'
    }
}

Step 'Done'
if ($dryRun) { Say 'WhatIf: nothing was changed.' }
else {
    Say "Rigbay $version is installed in $Root."
    if (-not $NoShortcut) { Say 'Start it from the Start menu (it asks for administrator permission, that is expected).' }
    else { Say "Start it with: $rootExe" }
    Say 'First start: open Modules and install the modules you want; each one stays off until you turn it on.'
    Say 'Windows SmartScreen may warn about the first start, because the files are not signed.'
    if ($Relaunched) { Read-Host 'Press Enter to close this window' | Out-Null }
}
exit 0
