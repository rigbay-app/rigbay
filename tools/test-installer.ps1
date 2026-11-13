<#
.SYNOPSIS
  Tests install.ps1 and uninstall.ps1 of an extracted release zip in SCRATCH folders. Run it elevated
  (tools\e2e\invoke-elevated.ps1 -File tools\test-installer.ps1 -Arguments '-ZipDir','<extracted folder>').

.DESCRIPTION
  Everything goes to <Work>\... and to test-named objects: -Root, -DataRoot, -ShortcutDir, -UninstallKeyRoot HKCU:\Software\<test>,
  -TaskName <test name>, and -UserDataDir for the uninstall script (so -RemoveData cannot reach %LOCALAPPDATA%\Rigbay).
  It never uses C:\Program Files\Rigbay, C:\ProgramData\Rigbay, the task "Rigbay", the real Start menu or the real Uninstall key.
  Steps (docs/RELEASING.md, section 11.3): -WhatIf changes nothing; install; the exe's version; admin-only modules folder; the task
  XML equals the one the app writes itself; upgrade over a running instance (the installer has to quit it) keeping modules and
  settings; uninstall -WhatIf; uninstall keeps modules and settings; reinstall; -RemoveData; a non-empty foreign folder is refused
  by both scripts; both scripts started from a NON-elevated shell open an elevated one (-NoPause) - that last part is run by
  the caller's normal shell when -Unelevated is passed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipDir,
    [string]$Work,
    [switch]$Unelevated   # run only the non-elevated part (start this one from a normal shell)
)
$ErrorActionPreference = 'Continue'   # schtasks.exe and the scripts under test write to stderr on purpose
$ZipDir = (Resolve-Path $ZipDir).Path
if (-not $Work) { $Work = Join-Path $PSScriptRoot 'out\installer-test' }
$tag = 'RigbayTest' + ([Guid]::NewGuid().ToString('N').Substring(0, 6))
$passed = 0; $failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "PASS $name" } else { $script:failed++; Write-Host "FAIL $name :: $detail" -ForegroundColor Red }
}
function Section($t) { Write-Host ''; Write-Host "== $t" }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Unelevated -and $isAdmin) { throw '-Unelevated must be started from a normal (not elevated) shell' }
if (-not $Unelevated -and -not $isAdmin) { throw 'run this elevated (tools\e2e\invoke-elevated.ps1), or pass -Unelevated for the non-elevated part' }

# the real things that must not change
function RealState {
    $task = (& schtasks.exe /query /tn Rigbay 2>&1 | Out-String); $global:LASTEXITCODE = 0
    [pscustomobject]@{
        ProgramFiles = (Test-Path (Join-Path $env:ProgramFiles 'Rigbay'))
        ProgramData  = (Test-Path (Join-Path $env:ProgramData 'Rigbay'))
        Task         = ($task -notmatch 'ERROR')
        StartMenu    = (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Rigbay.lnk'))
        UninstallKey = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Rigbay')
        Running      = @(Get-Process Rigbay.App -ErrorAction SilentlyContinue).Count
    } | ConvertTo-Json -Compress
}
$realBefore = RealState
Write-Host "real state before: $realBefore"

if (-not $Unelevated) {
    $psExe = (Get-Process -Id $PID).Path
    function Case([string]$name) {   # fresh scratch names per case
        $c = @{}
        $c.Root = Join-Path $Work "$name\Program Files\Rigbay"
        $c.Data = Join-Path $Work "$name\ProgramData\Rigbay"
        $c.Menu = Join-Path $Work "$name\Menu"
        $c.User = Join-Path $Work "$name\LocalAppData\Rigbay"
        $c.Key = "HKCU:\Software\$tag-$name"
        $c.Task = "$tag-$name"
        New-Item -ItemType Directory -Force $c.Menu | Out-Null
        $c.Args = @('-Root', $c.Root, '-DataRoot', $c.Data, '-ShortcutDir', $c.Menu, '-UninstallKeyRoot', $c.Key, '-TaskName', $c.Task)
        $c
    }
    function Install($c, [string[]]$extra = @()) { $o = & $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ZipDir 'install.ps1') @($c.Args) -NoPause @extra *>&1 | Out-String; [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o } }
    function Uninstall($c, [string[]]$extra = @()) { $o = & $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ZipDir 'uninstall.ps1') -Root $c.Root -DataRoot $c.Data -UserDataDir $c.User -ShortcutDir $c.Menu -UninstallKeyRoot $c.Key -TaskName $c.Task -NoPause @extra *>&1 | Out-String; [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o } }
    function TaskExists($n) { $o = & schtasks.exe /query /tn $n 2>&1 | Out-String; $global:LASTEXITCODE = 0; $o -notmatch 'ERROR' }
    function Snapshot($c) { [pscustomobject]@{ Root = (Test-Path $c.Root); Data = (Test-Path $c.Data); Lnk = (Test-Path (Join-Path $c.Menu 'Rigbay.lnk')); Key = (Test-Path (Join-Path $c.Key 'Rigbay')); Task = (TaskExists $c.Task) } }
    function Snap($c) { (Snapshot $c | ConvertTo-Json -Compress) }

    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $Work | Out-Null
    $exeSrc = Join-Path $ZipDir 'app\Rigbay.App.exe'
    Check 'the zip folder has install.ps1, uninstall.ps1 and app\Rigbay.App.exe' ((Test-Path (Join-Path $ZipDir 'install.ps1')) -and (Test-Path (Join-Path $ZipDir 'uninstall.ps1')) -and (Test-Path $exeSrc))

    # ------------------------------------------------------------------------------------------------ 1. -WhatIf changes nothing
    Section '1. install -WhatIf'
    $a = Case 'whatif'
    $before = Snap $a
    $r = Install $a @('-WhatIf')
    Check 'install -WhatIf: exit 0, nothing created (folder, data, shortcut, key, task)' ($r.Code -eq 0 -and (Snap $a) -eq $before -and -not (Test-Path $a.Root)) "code=$($r.Code) snapshot=$(Snap $a)"
    Check 'install -WhatIf: names the steps it would take' ($r.Out -match 'What if' -and $r.Out -match ('Rigbay ' + [regex]::Escape((Get-Item $exeSrc).VersionInfo.ProductVersion))) ($r.Out.Substring(0, [Math]::Min(300, $r.Out.Length)))

    # ------------------------------------------------------------------------------------------------ 2. install
    Section '2. install'
    $b = Case 'main'
    $r = Install $b
    $snap = Snapshot $b
    Check 'install: exit 0' ($r.Code -eq 0) "code=$($r.Code)`n$($r.Out)"
    Check 'install: program folder, data folder, shortcut, Installed apps entry and the task all exist' ($snap.Root -and $snap.Data -and $snap.Lnk -and $snap.Key -and $snap.Task) (Snap $b)
    $exe = Join-Path $b.Root 'Rigbay.App.exe'
    $ver = (& $exe --version 2>&1 | Out-String).Trim()
    Check 'install: the installed exe says "Rigbay 0.1.3"' ($ver -eq 'Rigbay 0.1.3') $ver
    $same = (Get-FileHash $exe).Hash -eq (Get-FileHash $exeSrc).Hash
    Check 'install: the installed exe is byte-equal to the one in the zip' $same
    $uk = Get-ItemProperty (Join-Path $b.Key 'Rigbay') -ErrorAction SilentlyContinue
    Check 'install: the Installed apps entry has name, version 0.1.3 and an uninstall command' ($uk -and $uk.DisplayName -eq 'Rigbay' -and $uk.DisplayVersion -eq '0.1.3' -and $uk.UninstallString) ($uk | Out-String)
    # admin-only modules folder: only Administrators and SYSTEM may have an allow entry, none inherited
    $modules = Join-Path $b.Data 'modules'
    $acl = Get-Acl $modules
    $names = @($acl.Access | ForEach-Object { $_.IdentityReference.Value })
    $bad = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM' })
    Check 'install: the modules folder is writable only by Administrators and SYSTEM, access rules protected' ((Test-Path $modules) -and $bad.Count -eq 0 -and $acl.AreAccessRulesProtected) ("entries: " + ($names -join ', ') + "; protected=$($acl.AreAccessRulesProtected)")

    # ------------------------------------------------------------------------------------------------ 3. the task XML equals the app's own
    Section '3. the scheduled task'
    $other = "$tag-own"
    $dataOwn = Join-Path $Work 'own-data'
    $p = Start-Process $exe -ArgumentList @('--data', "`"$dataOwn`"", '--modules', "`"$modules`"", '--task-name', $other, '--script', 'startup:on;wait:800', '--exit-after', '2500', '--allow-multi') -PassThru -Wait
    $ownExists = TaskExists $other
    function NormXml($name) {
        $x = (& schtasks.exe /query /tn $name /xml | Out-String)
        $global:LASTEXITCODE = 0
        $x = [regex]::Replace($x, '<(Date|URI|Author|UserId|StartBoundary|Description)>.*?</\1>', '')
        $x = $x.Replace($name, '<NAME>')
        (($x -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join "`n")
    }
    if ($ownExists) {
        $xa = NormXml $b.Task; $xb = NormXml $other
        Check 'task: the XML the installer wrote equals the one the app writes itself (dates, URI, user ids and names left out)' ($xa -eq $xb) ("installer:`n$xa`napp:`n$xb")
        $xml = & schtasks.exe /query /tn $b.Task /xml | Out-String; $global:LASTEXITCODE = 0
        Check 'task: logon trigger, 15 s delay, highest privileges, starts the installed exe with --tray' ($xml -match '<LogonTrigger>' -and $xml -match 'PT15S' -and $xml -match 'HighestAvailable' -and $xml.Contains($exe) -and $xml -match '--tray') ''
        & schtasks.exe /delete /tn $other /f 2>&1 | Out-Null; $global:LASTEXITCODE = 0
    }
    else { Check 'task: the app wrote its own test task (startup:on)' $false "exit $($p.ExitCode), no task $other" }
    Remove-Item $dataOwn -Recurse -Force -ErrorAction SilentlyContinue

    # ------------------------------------------------------------------------------------------------ 4. upgrade over a running instance
    Section '4. upgrade over a running instance, modules and settings kept'
    $marker = Join-Path $modules 'marker-module'
    New-Item -ItemType Directory $marker -Force | Out-Null
    Set-Content (Join-Path $marker 'x.txt') 'keep me'
    $userData = Join-Path $b.User 'x'
    New-Item -ItemType Directory $b.User -Force | Out-Null
    Set-Content (Join-Path $b.User 'settings.json') '{"kept":true}'
    Set-Content (Join-Path $b.Root 'stale-from-old-version.txt') 'old file'
    $app = Start-Process $exe -ArgumentList @('--data', "`"$($b.User)`"", '--modules', "`"$modules`"", '--task-name', "$tag-run", '--allow-multi') -PassThru
    Start-Sleep -Seconds 6
    $alive = -not $app.HasExited
    Check 'upgrade: a Rigbay started from the install folder is running' $alive
    $r = Install $b
    Start-Sleep -Milliseconds 500
    $stillThere = @(Get-Process -Id $app.Id -ErrorAction SilentlyContinue).Count
    Check 'upgrade: exit 0 and the running instance was quit by the installer' ($r.Code -eq 0 -and $stillThere -eq 0) "code=$($r.Code) running=$stillThere`n$($r.Out)"
    Check 'upgrade: the modules folder and its content, and the settings, are kept' ((Test-Path (Join-Path $marker 'x.txt')) -and (Test-Path (Join-Path $b.User 'settings.json'))) ''
    Check 'upgrade: the exe is still the zip''s, the task, shortcut and entry still exist' (((Get-FileHash $exe).Hash -eq (Get-FileHash $exeSrc).Hash) -and (Snapshot $b).Task -and (Snapshot $b).Lnk -and (Snapshot $b).Key) (Snap $b)

    # ------------------------------------------------------------------------------------------------ 5. uninstall
    Section '5. uninstall'
    $before = Snap $b
    $r = Uninstall $b @('-WhatIf')
    Check 'uninstall -WhatIf: exit 0, nothing changed' ($r.Code -eq 0 -and (Snap $b) -eq $before -and (Test-Path $modules)) "code=$($r.Code) $(Snap $b)"
    $app = Start-Process $exe -ArgumentList @('--data', "`"$($b.User)`"", '--modules', "`"$modules`"", '--task-name', $b.Task, '--allow-multi') -PassThru
    Start-Sleep -Seconds 6
    $r = Uninstall $b
    Start-Sleep -Milliseconds 500
    $s = Snapshot $b
    Check 'uninstall (with a running instance): exit 0, instance gone, folder, shortcut, entry and task removed' ($r.Code -eq 0 -and @(Get-Process -Id $app.Id -ErrorAction SilentlyContinue).Count -eq 0 -and -not $s.Root -and -not $s.Lnk -and -not $s.Key -and -not $s.Task) "code=$($r.Code) $(Snap $b)`n$($r.Out)"
    Check 'uninstall: modules and settings are kept' ((Test-Path (Join-Path $marker 'x.txt')) -and (Test-Path (Join-Path $b.User 'settings.json'))) ''
    $r = Install $b
    Check 'reinstall after uninstall: exit 0, everything back, the old modules still there' ($r.Code -eq 0 -and (Snapshot $b).Root -and (Snapshot $b).Task -and (Test-Path (Join-Path $marker 'x.txt'))) "code=$($r.Code)`n$($r.Out)"
    $r = Uninstall $b @('-RemoveData')
    Check 'uninstall -RemoveData: the data folder and the user data folder are gone too' ($r.Code -eq 0 -and -not (Test-Path $b.Data) -and -not (Test-Path $b.User) -and -not (Test-Path $b.Root)) "code=$($r.Code) data=$(Test-Path $b.Data) user=$(Test-Path $b.User)"
    Check 'uninstall -RemoveData: only the test folders were touched (the real state is unchanged)' ((RealState) -eq $realBefore) "before=$realBefore after=$(RealState)"

    # ------------------------------------------------------------------------------------------------ 6. a foreign folder is refused
    Section '6. a non-empty foreign folder'
    $f = Case 'foreign'
    New-Item -ItemType Directory $f.Root -Force | Out-Null
    Set-Content (Join-Path $f.Root 'precious.txt') 'not rigbay'
    $r = Install $f
    Check 'install into a non-empty folder that is not a Rigbay install: refused, nothing changed' ($r.Code -ne 0 -and (Test-Path (Join-Path $f.Root 'precious.txt')) -and -not (Test-Path (Join-Path $f.Root 'Rigbay.App.exe')) -and -not (Snapshot $f).Task -and -not (Snapshot $f).Key) "code=$($r.Code)`n$($r.Out)"
    $r = Uninstall $f
    Check 'uninstall on that folder: nothing deleted (it is not a Rigbay install)' ((Test-Path (Join-Path $f.Root 'precious.txt'))) "code=$($r.Code)`n$($r.Out)"
    $r = Install $f @('-WhatIf')
    Check 'install -WhatIf on that folder: also refused, nothing changed' ($r.Code -ne 0 -and (Test-Path (Join-Path $f.Root 'precious.txt'))) "code=$($r.Code)"
}
else {
    # ------------------------------------------------------------------------------------------------ 7. from a NON-elevated shell
    Section '7. both scripts from a non-elevated shell (they open an elevated window; -NoPause closes it)'
    $psExe = (Get-Process -Id $PID).Path
    $name = 'unelevated'
    $root = Join-Path $Work "$name\Program Files\Rigbay"; $data = Join-Path $Work "$name\ProgramData\Rigbay"; $menu = Join-Path $Work "$name\Menu"; $user = Join-Path $Work "$name\LocalAppData\Rigbay"
    $key = "HKCU:\Software\$tag-$name"; $task = "$tag-$name"
    Remove-Item (Join-Path $Work $name) -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $menu | Out-Null
    $r = (& $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ZipDir 'install.ps1') -Root $root -DataRoot $data -ShortcutDir $menu -UninstallKeyRoot $key -TaskName $task -NoPause -WhatIf *>&1 | Out-String)
    Check 'non-elevated install -WhatIf: reads only, nothing created' ($LASTEXITCODE -eq 0 -and -not (Test-Path $root) -and $r -match 'not elevated') "code=$LASTEXITCODE`n$r"
    $r = (& $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ZipDir 'install.ps1') -Root $root -DataRoot $data -ShortcutDir $menu -UninstallKeyRoot $key -TaskName $task -NoPause *>&1 | Out-String)
    $code = $LASTEXITCODE
    Check 'non-elevated install: opened an elevated window and installed' ($code -eq 0 -and (Test-Path (Join-Path $root 'Rigbay.App.exe')) -and (Test-Path (Join-Path $key 'Rigbay'))) "code=$code`n$r"
    $r = (& $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ZipDir 'uninstall.ps1') -Root $root -DataRoot $data -UserDataDir $user -ShortcutDir $menu -UninstallKeyRoot $key -TaskName $task -NoPause *>&1 | Out-String)
    $code = $LASTEXITCODE
    Check 'non-elevated uninstall: opened an elevated window and removed it (the data folder is admin-only, so this shell cannot look inside)' ($code -eq 0 -and -not (Test-Path $root) -and -not (Test-Path (Join-Path $key 'Rigbay'))) "code=$code`n$r"
    Check 'the real state is unchanged' ((RealState) -eq $realBefore) "before=$realBefore after=$(RealState)"
}

Section 'Result'
Write-Host "installer test: $passed passed, $failed failed"
Write-Host "real state after: $(RealState)"
exit ([int]($failed -gt 0))
