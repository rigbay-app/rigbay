# E2E1 main flow: the first hour of a user, in SCRATCH folders, against the REAL GitHub registry and the REAL module releases.
# Runs ELEVATED (tools\e2e\run.ps1 starts it). The app is the exe extracted from the released zip (never tools\out\app, never C:\Program Files).
#
#   fresh scratch -> first start -> Get the module list (github) -> Install recorder -> Undo -> Install recorder + predatorsense -> Restart now
#   -> both installed and OFF -> enable predatorsense (dialog, reads only) -> enable recorder, record 10 s to a scratch folder, check the CSV
#   -> Remove recorder -> Restart now -> gone, files cleaned
#
# Nothing here presses a hardware control: the PredatorSense page is only read (fan mode box, Cool Booster and shortcut rows are never touched).
# Evidence: <Out>\<theme>-*.png (PrintWindow of the app's own HWND), <Out>\report-<theme>.txt, the run's log copy.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,                 # tools\out\e2e
    [ValidateSet('dark', 'light')][string]$Theme = 'dark',
    [string]$Out,
    [string]$AppDir                                      # default: <Root>\app, the released zip extracted by run.ps1
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
if (-not $Out) { $Out = Join-Path $Root 'evidence' }
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not $AppDir) { $AppDir = Join-Path $Root 'app' }
$exe = Join-Path $AppDir 'Rigbay.App.exe'
$Run = Join-Path $Root "run-$Theme"
$script:Base = $Run
$data = Join-Path $Run 'data'
$mods = Join-Path $Run 'modules'
$inst = Join-Path $Run 'modules.install'
$recDir = Join-Path $Run 'recordings'
$script:ReportFile = Join-Path $Out "report-$Theme.txt"
Remove-Item $script:ReportFile -ErrorAction SilentlyContinue
$script:Pass = 0; $script:Fail = 0

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++ } else { $script:Fail++ }
    Say ('{0}  {1}{2}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { '  -- ' + $Detail } else { '' }))
}
function Shot($Win, [string]$Name) { [void](Take-Shot $Win (Join-Path $Out "$Theme-$Name.png")) }
function Nav($Win, [string]$Item) { [void](Invoke-El (Find-El $Win $Item $script:CT::ListItem 8)); Start-Sleep -Milliseconds 1200 }
function Json($Path) { if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function Pend { $j = Json (Join-Path $inst 'pending.json'); [pscustomobject]@{ ops = $(if ($j -and $j.ops) { @($j.ops) } else { @() }) } }
function Enabled { $j = Json (Join-Path $data 'host.json'); $r = @(); if ($j -and $j.PSObject.Properties['data'] -and $j.data.PSObject.Properties['enabledModules']) { $r = @($j.data.enabledModules) }; , $r }
function Names($Dir) { $r = @(); if (Test-Path -LiteralPath $Dir) { $r = @(Get-ChildItem -LiteralPath $Dir -Force | ForEach-Object Name) }; , $r }
function Has($Win, [string]$Text, $Type = $null) { Test-El $Win $Text $Type -Contains }

# ---------------------------------------------------------------- what must NOT change on this PC
function Get-RealState {
    $s = New-Object System.Collections.Generic.List[string]
    $s.Add('ProgramData\Rigbay exists: ' + (Test-Path 'C:\ProgramData\Rigbay'))
    $s.Add('Program Files\Rigbay exists: ' + (Test-Path 'C:\Program Files\Rigbay'))
    $t = & cmd /c 'schtasks /query /tn Rigbay >nul 2>&1 && echo present || echo absent'
    $s.Add('scheduled task Rigbay: ' + $t)
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $props = Get-ItemProperty -Path $k
    foreach ($n in 'AutoConfigURL', 'ProxyEnable', 'ProxyServer') {
        $pp = $props.PSObject.Properties[$n]
        $v = if ($pp) { [string]$pp.Value } else { '<not set>' }
        # a proxy address can be personal: the report keeps only a short hash of it, which is enough to see a change
        $h = [BitConverter]::ToString((New-Object Security.Cryptography.SHA1Managed).ComputeHash([Text.Encoding]::UTF8.GetBytes($v))).Replace('-', '').Substring(0, 8)
        $s.Add("HKCU Internet Settings $n : length $($v.Length), sha1 $h")
    }
    $logs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Rigbay\logs'
    $s.Add('Documents\Rigbay\logs: ' + $(if (Test-Path $logs) { (@(Get-ChildItem $logs -Force | ForEach-Object { $_.Name + ':' + $_.Length }) -join ',') } else { 'absent' }))
    $local = Join-Path $env:LOCALAPPDATA 'Rigbay'
    $s.Add('LOCALAPPDATA\Rigbay: ' + $(if (Test-Path $local) { (@(Get-ChildItem $local -Recurse -Force -File | ForEach-Object { $_.FullName.Substring($local.Length) + ':' + $_.Length + ':' + $_.LastWriteTimeUtc.Ticks }) -join ',') } else { 'absent' }))
    $s
}

Say "== E2E1 main flow, theme=$Theme, run folder $Run"
Say "app exe: $exe  (version $((Get-Item $exe).VersionInfo.ProductVersion))"
$realBefore = Get-RealState
$realBefore | ForEach-Object { Say "  real before: $_" }

# fresh scratch (admin-only folders left by an earlier run need an elevated delete: this process is elevated)
foreach ($d in $Run) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force $Run | Out-Null
Stop-MyApps

$registry = $null
try { $registry = Invoke-RestMethod 'https://raw.githubusercontent.com/rigbay-app/registry/main/registry.json' -UseBasicParsing } catch { Say "could not read the registry directly for comparison: $_" }
$regOf = @{}; if ($registry) { foreach ($m in $registry.modules) { $regOf[$m.id] = $m } }
# the version of a module the app installs is the newest one the registry lists (a module release after this script was written must not break it)
function RegVersion([string]$id, [string]$fallback = '0.1.0') { if ($regOf.ContainsKey($id)) { [string]$regOf[$id].versions[-1].version } else { $fallback } }

try {
    # ------------------------------------------------------------ 1. first start
    $multi = @(Get-ForeignApps).Count -gt 0
    $appArgs = @('--data', $data, '--modules', $mods, '--theme', $Theme)
    if ($multi) { $appArgs += '--allow-multi'; Say "another Rigbay.App (not this run's) is running: --allow-multi so the single-instance slot is not shared" }
    $p = Start-App $exe $appArgs
    $w = Get-AppWindow $p.Id
    Check 'first start: the window appears' ($null -ne $w)
    Resize-Win $w 1240 1250
    Start-Sleep -Seconds 2
    Check 'first start opens the Modules page by itself' (Has $w 'Get the module list' $script:CT::Button) 'the button "Get the module list" is on screen without any navigation'
    Check 'the page shows the scratch modules folder, not ProgramData' (Has $w $mods) $mods
    Shot $w '01-first-start'
    Check 'nothing was created before the user asked: no install folder yet' (-not (Test-Path $inst)) ('present: ' + (Names $Run) -join ',')

    # ------------------------------------------------------------ 2. the module list from GitHub
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [void](Click-Button $w 'Get the module list')
    $ok = Wait-Until { Has $w 'Install Recorder' $script:CT::Button } 30

    Check 'registry fetched from GitHub through the page' $ok ("{0:N1} s" -f $sw.Elapsed.TotalSeconds)
    Start-Sleep -Milliseconds 600
    foreach ($m in 'PredatorSense', 'Recorder', 'Steam access', 'XTU profiles') { Check "list shows $m with an Install button" (Has $w "Install $m" $script:CT::Button) }
    Check 'the list says when it was checked' (Has $w 'Module list checked')
    foreach ($id in 'predatorsense', 'recorder', 'steam-bypass', 'xtu') { Check "registry entry $id exists on GitHub" ($regOf.ContainsKey($id)) }
    if ($regOf.ContainsKey('predatorsense')) {
        $v = $regOf['predatorsense'].versions[-1]
        Check 'PredatorSense row: size and declared needs match the registry' ((Has $w 'Needs administrator') -and (Has $w (($v.size / 1024.0).ToString('0.#', [Globalization.CultureInfo]::CurrentCulture) + ' KB'))) ("size {0} bytes" -f $v.size)
    }
    Shot $w '02-module-list'
    [void](Find-El $w 'PredatorSense' $script:CT::Button 3)
    $pb = $w.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.AndCondition (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'PredatorSense'), (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), $script:CT::Button))) | Select-Object -First 1
    if ($pb) { try { $pb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { } }
    Check 'details show the SHA-256, the source link and what it declares' ((Has $w 'SHA-256 of the zip') -and (Has $w 'github.com/rigbay-app/module-predatorsense') -and (Has $w 'AcerGamingFunction'))
    Check 'details say "Works on this PC: not known until it is installed"' (Has $w 'Not known until it is installed')
    Shot $w '03-details-predatorsense'
    if ($pb) { try { $pb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { } }
    # a second request is conditional (If-None-Match): GitHub answers 304 and the app keeps its copy
    [void](Click-Button $w 'Check for updates')
    Check 'Check for updates again: GitHub answers 304, the saved list is kept' (Wait-Until { Select-String -Path (Join-Path $data 'logs\rigbay.log') -Pattern 'registry: not modified' -Quiet } 20)

    # ------------------------------------------------------------ 3. install recorder (watch the states), look, undo
    $seen = New-Object System.Collections.Generic.List[string]
    [void](Click-Button $w 'Install Recorder')
    $sw = [Diagnostics.Stopwatch]::StartNew(); $shotTaken = $false; $last = ''
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $t = (Get-Texts $w | Where-Object { $_ -match 'Download|Verif|Unpack|Check|Cancel|Restart required|Appears after|Installed\.' } | ForEach-Object { $_ -replace '\s+', ' ' }) -join ' | '
        if ($t -ne $last) { $last = $t; $seen.Add(('{0:N2}s {1}' -f $sw.Elapsed.TotalSeconds, $t)) }
        if (-not $shotTaken -and $t -match 'Downloading') { Shot $w '04-installing-recorder'; $shotTaken = $true }
        if ($t -match 'Appears after the restart') { break }
        Start-Sleep -Milliseconds 30
    }
    $seen | ForEach-Object { Say "  state: $_" }
    Check 'install recorder: reaches "Installed. Appears after the restart, turned off."' ($last -match 'Appears after the restart') ("{0:N1} s" -f $sw.Elapsed.TotalSeconds)
    Check 'a progress state was visible while it ran' (($seen -join ' ') -match 'Downloading|Busy|Cancel') 'real download of a 73 KB zip takes under a second'
    Start-Sleep -Milliseconds 800
    Check 'amber "Restart required" bar with Restart now' ((Has $w 'Restart required') -and (Has $w 'Restart now' $script:CT::Button))
    Shot $w '05-restart-required'
    $pend = Pend
    $op = @($pend.ops) | Select-Object -First 1
    Check 'pending.json has the install, with the registry SHA-256' ($op -and $op.op -eq 'install' -and $op.id -eq 'recorder' -and ($regOf.ContainsKey('recorder') -and $op.sha256 -eq $regOf['recorder'].versions[-1].sha256)) ($op.sha256)
    Check 'the modules folder is still untouched (recorder is parked in ready\)' ((-not (Test-Path (Join-Path $mods 'recorder'))) -and (Test-Path (Join-Path $inst 'ready\recorder\module.json'))) ('modules: ' + ((Names $mods) -join ','))
    Check 'downloads\ and staging\ are empty after the install' (((Names (Join-Path $inst 'downloads')).Count -eq 0) -and ((Names (Join-Path $inst 'staging')).Count -eq 0))
    $accounts = & icacls $mods
    Check 'ACL of the scratch modules folder: Administrators and SYSTEM only' (($accounts -match 'BUILTIN\\Administrators') -and ($accounts -match 'NT AUTHORITY\\SYSTEM') -and -not ($accounts -match ('Users|Everyone|' + [regex]::Escape($env:USERNAME) + '|Authenticated'))) ((($accounts | Where-Object { $_ -match '\(' }) -join ' ') -replace '\s+', ' ')

    [void](Click-Button $w 'Undo the change to Recorder')
    Start-Sleep -Seconds 2
    $pend = Pend
    Check 'Undo: the change is gone, the staged files are deleted, the Install button is back' (((@($pend.ops)).Count -eq 0) -and (-not (Test-Path (Join-Path $inst 'ready\recorder'))) -and (Has $w 'Install Recorder' $script:CT::Button) -and -not (Has $w 'Restart now' $script:CT::Button))
    Shot $w '06-after-undo'

    # ------------------------------------------------------------ 4. install both, restart
    [void](Click-Button $w 'Install Recorder')
    [void](Wait-Until { Has $w 'Restart now' $script:CT::Button } 30)
    Start-Sleep -Milliseconds 800
    [void](Click-Button $w 'Install PredatorSense')
    Check 'second install queues next to the first' (Wait-Until { (@((Pend).ops)).Count -eq 2 } 30)
    Start-Sleep -Milliseconds 800
    Shot $w '07-both-queued'
    $pend = Pend
    $okHash = $true
    foreach ($o in @($pend.ops)) { if (-not ($regOf.ContainsKey($o.id) -and $o.sha256 -eq $regOf[$o.id].versions[-1].sha256)) { $okHash = $false } }
    Check 'both queued changes carry the registry SHA-256' $okHash
    Check 'steam-bypass and xtu were never installed or queued' (-not (@($pend.ops) | Where-Object { $_.id -in 'xtu', 'steam-bypass' }) -and -not (Test-Path (Join-Path $inst 'ready\xtu')) -and -not (Test-Path (Join-Path $inst 'ready\steam-bypass')))

    $oldPid = $p.Id
    [void](Click-Button $w 'Restart now')
    $new = Wait-NewApp @($oldPid) 60
    Check 'Restart now: a new Rigbay.App process starts' ($null -ne $new)
    $oldGone = Wait-Until { -not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) } 20
    Check 'Restart now: the old process ended' $oldGone
    $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($new.Id)").CommandLine
    Say "  successor command line: $cmd"
    Check 'successor runs the same exe with the same scratch data and modules folders' (($cmd -like "*$exe*") -and ($cmd -like "*--data $data*") -and ($cmd -like "*--modules $mods*") -and ($cmd -like "*--wait-pid $oldPid*"))
    $p = $new
    $w = Get-AppWindow $p.Id
    Resize-Win $w 1240 1250
    Start-Sleep -Seconds 3
    Check 'after restart: Dashboard lists both modules, Off' ((Has $w 'PredatorSense, Off' $script:CT::Button) -and (Has $w 'Recorder, Off' $script:CT::Button)) ((Get-Texts $w | Where-Object { $_ -match ', Off' }) -join ' | ')
    Shot $w '08-after-restart-dashboard'
    Check 'modules\ now holds both, ready\ and pending are empty' (((Names $mods) -contains 'recorder') -and ((Names $mods) -contains 'predatorsense') -and ((Names (Join-Path $inst 'ready')).Count -eq 0) -and ((@((Pend).ops)).Count -eq 0)) ((Names $mods) -join ',')
    Nav $w 'Modules'
    Check 'Modules page reports what the start applied, both installed turned off' ((Has $w 'At the last start') -and (Has $w ('Recorder ' + (RegVersion 'recorder') + ' was installed')) -and (Has $w ('PredatorSense ' + (RegVersion 'predatorsense') + ' was installed')))
    Check 'both switches are off' (-not (Get-Toggle (Find-El $w 'Enable PredatorSense' $script:CT::Button)) -and -not (Get-Toggle (Find-El $w 'Enable Recorder' $script:CT::Button)))
    Check 'host.json: nothing is enabled yet' ((Enabled).Count -eq 0)
    Shot $w '09-after-restart-modules'

    # ------------------------------------------------------------ 5. PredatorSense: enable through the dialog, READ only
    Nav $w 'PredatorSense'
    Shot $w '10-predatorsense-off'
    $tg = Find-El $w 'Enable PredatorSense' $script:CT::Button
    $tg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 1500
    Check 'turn-on dialog names the declarations: administrator, the Acer service, the WMI class' ((Has $w 'Turn on PredatorSense?') -and (Has $w 'Administrator rights') -and (Has $w 'AcerGamingFunction') -and (Has $w 'PSSvc') -and (Has $w 'does not enforce it'))
    Shot $w '11-predatorsense-turn-on-dialog'
    [void](Click-Button $w 'Turn on')
    $ok = Wait-Until { Has $w 'CPU temperature' $script:CT::Text } 20
    Start-Sleep -Seconds 4
    Check 'PredatorSense turned on: its page shows Readings' $ok
    $texts = @(Get-Texts $w | ForEach-Object { $_ -replace '^\[[A-Za-z]+\] ', '' })
    $cpu = $texts | Where-Object { $_ -match '^\d+\s\W*C$' } | Select-Object -First 3
    $rpm = $texts | Where-Object { $_ -match '^\d+ rpm$' }
    Check 'live readings: three temperatures and three fan speeds, all numbers' ((@($cpu).Count -ge 3) -and (@($rpm).Count -ge 3)) (("{0} | {1}" -f (@($cpu) -join ', '), (@($rpm) -join ', ')))
    $tv = @($cpu | ForEach-Object { [int]($_ -replace '\D.*', '') })
    Check 'temperatures are plausible (10..110)' (@($tv | Where-Object { $_ -lt 10 -or $_ -gt 110 }).Count -eq 0) ($tv -join ',')
    Check 'the machine line is the firmware model string' (Has $w 'Predator')
    Check 'no write: "Last set by Rigbay" says Nothing yet and the speed check says No change made yet' ((Has $w 'Nothing yet') -and (Has $w 'No change made yet'))
    Shot $w '12-predatorsense-readings'
    Nav $w 'Dashboard'
    Shot $w '13-dashboard-predatorsense-on'
    Check 'host.json: predatorsense is enabled' ((Enabled) -contains 'predatorsense')

    # ------------------------------------------------------------ 6. recorder: enable, record 10 s into a scratch folder, check the CSV
    Nav $w 'Recorder'
    $tg = Find-El $w 'Enable Recorder' $script:CT::Button
    $tg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 1500
    Check 'recorder dialog lists its two registry reads' ((Has $w 'Turn on Recorder?') -and (Has $w 'HARDWARE\DESCRIPTION\System\BIOS') -and (Has $w 'CentralProcessor'))
    Shot $w '14-recorder-turn-on-dialog'
    [void](Click-Button $w 'Turn on')
    Check 'recorder turned on: Start is there' (Wait-Until { Has $w 'Start' $script:CT::Button } 15)
    Start-Sleep -Seconds 1
    # the default folder is Documents\Rigbay\logs on the REAL profile (finding E2E-02): point it at the scratch folder first
    $ed = Find-El $w 'Folder' $script:CT::Edit
    $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($recDir)
    Start-Sleep -Milliseconds 700
    [void](Click-Button $w 'Apply')
    Start-Sleep -Milliseconds 800
    Shot $w '15-recorder-page'
    $before = Get-Date
    [void](Click-Button $w 'Start')
    Check 'recorder: State becomes Recording' (Wait-Until { Has $w 'Recording to' } 10)
    Start-Sleep -Seconds 5
    Shot $w '16-recorder-running'
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 5
    [void](Click-Button $w 'Stop')
    Start-Sleep -Seconds 2
    Shot $w '17-recorder-stopped'
    $csv = @(Get-ChildItem -LiteralPath $recDir -Filter '*.csv' -ErrorAction SilentlyContinue)
    Check 'exactly one CSV and one .meta.json in the scratch folder' (($csv.Count -eq 1) -and (@(Get-ChildItem -LiteralPath $recDir -Filter '*.meta.json').Count -eq 1)) ((Names $recDir) -join ', ')
    if ($csv.Count -eq 1) {
        $lines = @(Get-Content -LiteralPath $csv[0].FullName -Encoding UTF8)
        $head = $lines[0]
        Say "  CSV header: $head"
        Check 'CSV header: timestamp, elapsed_s and the six PredatorSense metrics' (($head -split ',').Count -eq 8 -and $head.StartsWith('timestamp,elapsed_s,') -and $head -match 'cpu\.temp' -and $head -match 'fan\.cpu\.rpm' -and $head -match 'gpu\.temp')
        $rows = @($lines | Select-Object -Skip 1)
        Check 'CSV has 10 to 13 rows for a 10 s recording at 1 s' (($rows.Count -ge 10) -and ($rows.Count -le 13)) ("$($rows.Count) rows")
        $stamps = @($rows | ForEach-Object { [DateTimeOffset]::Parse(($_ -split ',')[0]) })
        $gaps = @(for ($i = 1; $i -lt $stamps.Count; $i++) { ($stamps[$i] - $stamps[$i - 1]).TotalSeconds })
        Check 'rows are one second apart (every gap 1.000 +- 0.05 s)' (@($gaps | Where-Object { [Math]::Abs($_ - 1) -gt 0.05 }).Count -eq 0) (('min {0:N3} max {1:N3}' -f ($gaps | Measure-Object -Minimum).Minimum, ($gaps | Measure-Object -Maximum).Maximum))
        Check 'no empty cells and every value numeric' (@($rows | Where-Object { $c = $_ -split ','; @($c | Where-Object { $_ -eq '' }).Count -gt 0 -or @($c[1..7] | Where-Object { $_ -notmatch '^-?\d+(\.\d+)?$' }).Count -gt 0 }).Count -eq 0) ($rows[0])
        $bytes = [IO.File]::ReadAllBytes($csv[0].FullName)
        Check 'CSV is UTF-8 without a BOM' (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF))
        $meta = Json ($csv[0].FullName -replace '\.csv$', '.meta.json')
        Check '.meta.json: rows equals the CSV, stop reason "user", no error, the machine and both module versions' (($meta.rows -eq $rows.Count) -and $meta.stop_reason -eq 'user' -and -not $meta.error -and $meta.machine.model -and $meta.modules.predatorsense -eq (RegVersion 'predatorsense') -and $meta.modules.recorder -eq (RegVersion 'recorder')) ("rows=$($meta.rows) model=$($meta.machine.model)")
        Check '.meta.json: the degree sign in the unit is real UTF-8' ((@($meta.columns | Where-Object { $_.unit -eq ([string][char]0xB0 + 'C') })).Count -eq 3)
        Copy-Item $csv[0].FullName (Join-Path $Out "$Theme-recording.csv")
        Copy-Item ($csv[0].FullName -replace '\.csv$', '.meta.json') (Join-Path $Out "$Theme-recording.meta.json")
    }
    $realLogs = (Get-RealState | Where-Object { $_ -like 'Documents\Rigbay\logs*' }) -join ''
    Check 'the real Documents\Rigbay\logs did not change' ($realLogs -eq (($realBefore | Where-Object { $_ -like 'Documents\Rigbay\logs*' }) -join '')) 'the recording went to the scratch folder'

    # ------------------------------------------------------------ 7. remove the recorder, restart, gone
    Nav $w 'Modules'
    Check 'Modules page: both Installed rows say Running' ((@(Get-Texts $w | Where-Object { $_ -match '\] Running' })).Count -ge 2)
    Check 'installed row found and expanded' (Open-InstalledRow $w 'Recorder')
    Shot $w '18-installed-row-expanded'
    [void](Click-Button $w 'Remove Recorder')
    Start-Sleep -Milliseconds 1500
    Check 'remove asks first: "Remove Recorder?", the settings are kept' ((Has $w 'Remove Recorder?') -and (Has $w 'settings are kept'))
    Shot $w '19-remove-dialog'
    [void](Click-Button $w 'Remove')
    Start-Sleep -Seconds 2
    $pend = Pend
    Check 'removal is queued, nothing deleted yet' ((@($pend.ops)).Count -eq 1 -and $pend.ops[0].op -eq 'remove' -and (Test-Path (Join-Path $mods 'recorder\module.json')))
    Check 'row says "Is removed after the restart" and offers Undo' ((Has $w 'Is removed after the restart') -and (Has $w 'Undo the change to Recorder' $script:CT::Button))
    Shot $w '20-remove-queued'
    $oldPid = $p.Id
    [void](Click-Button $w 'Restart now')
    $new = Wait-NewApp @($oldPid) 60
    Check 'second Restart now: new process' ($null -ne $new)
    [void](Wait-Until { -not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) } 20)
    $p = $new
    $w = Get-AppWindow $p.Id
    Resize-Win $w 1240 1250
    Start-Sleep -Seconds 4
    Nav $w 'Modules'
    Check 'Modules page: "At the last start" says the recorder was removed' ((Has $w 'At the last start') -and (Has $w 'was removed'))
    Say ('  report line: ' + ((Get-Texts $w | Where-Object { $_ -match 'was removed' }) -join ' | '))
    Check 'the removal report names the module by its display name "Recorder" (finding E2E-04)' (@(Get-Texts $w) -clike '*] Recorder was removed*').Count -gt 0
    Check 'Recorder is gone from the Installed list and the nav pane' (-not (Has $w 'Enable Recorder' $script:CT::Button) -and -not (Test-El $w 'Recorder' $script:CT::ListItem))
    Check 'Recorder is offered again under Available' (Has $w 'Install Recorder' $script:CT::Button)
    Check 'PredatorSense came back Running (it stayed enabled)' (Has $w 'Running')
    Shot $w '21-after-remove-modules'
    Check 'files cleaned: modules\recorder deleted' (-not (Test-Path (Join-Path $mods 'recorder'))) ((Names $mods) -join ',')
    Check 'files cleaned: previous\, ready\, staging\, downloads\, trash\ all empty, pending has no change' (@(@('previous', 'ready', 'staging', 'downloads', 'trash') | Where-Object { (Names (Join-Path $inst $_)).Count -gt 0 }).Count -eq 0 -and (@((Pend).ops)).Count -eq 0) ((Names $inst) -join ',')
    Check 'host.json forgot the recorder, kept predatorsense' (((Enabled) -contains 'predatorsense') -and -not ((Enabled) -contains 'recorder'))
    $app = Json (Join-Path $data 'app.json'); Say ('  app.json: ' + ((Get-Content (Join-Path $data 'app.json') -Raw) -replace '\s+', ' '))
    Check 'by design the recorder settings and the recordings stay' ((Test-Path (Join-Path $data 'modules\recorder\settings.json')) -and (@(Get-ChildItem $recDir -Filter *.csv).Count -eq 1))
    Check 'the log never says ERROR' (-not (Select-String -Path (Join-Path $data 'logs\rigbay.log') -Pattern ' ERROR ' -Quiet)) ((Select-String -Path (Join-Path $data 'logs\rigbay.log') -Pattern ' (ERROR|WARN) ' | ForEach-Object Line) -join ' || ')
    Nav $w 'Dashboard'
    Shot $w '22-dashboard-final'
    Copy-Item (Join-Path $data 'logs\rigbay.log') (Join-Path $Out "$Theme-rigbay.log")
}
catch {
    Check 'flow ended without an exception' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber)
    try { if ($w) { Shot $w '99-exception' } } catch { }
}
finally {
    Stop-MyApps
    Start-Sleep -Seconds 1
    $realAfter = Get-RealState
    $same = (($realBefore -join "`n") -eq ($realAfter -join "`n"))
    Check 'the real PC is unchanged: no ProgramData\Rigbay, no Program Files\Rigbay, no task, same proxy settings, same Documents logs, same LOCALAPPDATA\Rigbay' $same
    if (-not $same) { Compare-Object $realBefore $realAfter | ForEach-Object { Say "  real diff: $($_.SideIndicator) $($_.InputObject)" } }
    Check 'no process of this run is left' (@(Get-MyApps).Count -eq 0)
    Say "== E2E1 main flow ($Theme): $($script:Pass) passed, $($script:Fail) failed"
}
exit ([int]($script:Fail -gt 0))
