# E2E1 hooks: the failure paths, in SCRATCH folders, with the app's own test switches and nothing else.
# Runs ELEVATED (tools\e2e\run.ps1 starts it). Uses the exe extracted from the released zip.
#
#   H1  offline, no saved list           the real registry address, but this one process is given a dead proxy (HTTPS_PROXY=127.0.0.1:9)
#   H2  offline with a saved list        list fetched from GitHub first, then the same scratch folders started with the dead proxy
#   H3  corrupted downloads              --registry <local file> (scratch folders only, the app's own switch) listing REAL release zips that were
#                                        damaged here: one byte flipped, cut in half, not a zip, wrong size in the list
#   H4  cancel                           --slow-download 1500 on the real PredatorSense zip, Cancel pressed mid-way
#   H5  a list that is not valid         --registry <broken json>
# The dead proxy is an environment variable of one child process: no adapter, no hosts file, no system proxy setting is touched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Out,
    [string]$AppDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
if (-not $Out) { $Out = Join-Path $Root 'evidence' }
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not $AppDir) { $AppDir = Join-Path $Root 'app' }
$exe = Join-Path $AppDir 'Rigbay.App.exe'
$script:ReportFile = Join-Path $Out 'report-hooks.txt'
Remove-Item $script:ReportFile -ErrorAction SilentlyContinue
$script:Pass = 0; $script:Fail = 0
$Work = Join-Path $Root 'hooks'
$script:Base = $Work          # Get-MyApps matches "--data <Work>\data...": every scenario's data folder is under it

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++ } else { $script:Fail++ }
    Say ('{0}  {1}{2}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { '  -- ' + $Detail } else { '' }))
}
function Shot($Win, [string]$Name) { [void](Take-Shot $Win (Join-Path $Out "hooks-$Name.png")) }
function Has($Win, [string]$Text, $Type = $null) { Test-El $Win $Text $Type -Contains }
function Names($Dir) { $r = @(); if (Test-Path -LiteralPath $Dir) { $r = @(Get-ChildItem -LiteralPath $Dir -Force | ForEach-Object Name) }; , $r }
function Json($Path) { if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function PendingCount($inst) { $j = Json (Join-Path $inst 'pending.json'); if ($j -and $j.ops) { @($j.ops).Count } else { 0 } }
function Row-Texts($Win) { @(Get-Texts $Win | ForEach-Object { $_ -replace '^\[[A-Za-z]+\] ', '' }) }

# one scenario = one scratch folder pair; returns the process, window and paths
function Start-Scenario([string]$Name, [string[]]$Extra = @(), [hashtable]$Env = @{}, [string]$ModulesRel = 'modules') {
    $dir = Join-Path $Work $Name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    return (Resume-Scenario $Name $Extra $Env $ModulesRel)
}
function Resume-Scenario([string]$Name, [string[]]$Extra = @(), [hashtable]$Env = @{}, [string]$ModulesRel = 'modules') {
    $dir = Join-Path $Work $Name
    $args2 = @('--data', (Join-Path $dir 'data'), '--modules', (Join-Path $dir $ModulesRel), '--theme', 'dark', '--first-run') + $Extra
    if (@(Get-ForeignApps).Count -gt 0) { $args2 += '--allow-multi' }
    foreach ($k in $Env.Keys) { Set-Item -Path "Env:$k" -Value $Env[$k] }
    try { $p = Start-App $exe $args2 } finally { foreach ($k in $Env.Keys) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue } }
    $w = Get-AppWindow $p.Id
    if ($w) { Resize-Win $w 1240 1250; Start-Sleep -Seconds 2 }
    $modsDir = Join-Path $dir $ModulesRel
    [pscustomobject]@{ P = $p; W = $w; Dir = $dir; Inst = ($modsDir + '.install'); Mods = $modsDir }
}

Say "== E2E1 hooks; app $exe (version $((Get-Item $exe).VersionInfo.ProductVersion)); scratch $Work"
Stop-MyApps
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force $Work | Out-Null
$DeadProxy = @{ HTTPS_PROXY = 'http://127.0.0.1:9'; HTTP_PROXY = 'http://127.0.0.1:9'; ALL_PROXY = 'http://127.0.0.1:9' }

try {
    # ============================================================ H1 offline, nothing saved
    Say '-- H1 offline, no saved list (real registry address, dead proxy for this process only)'
    $s = Start-Scenario 'h1-offline-none' @() $DeadProxy
    Check 'H1 window up on the Modules page (first run)' ($null -ne $s.W -and (Has $s.W 'Get the module list' $script:CT::Button))
    [void](Click-Button $s.W 'Get the module list')
    $ok = Wait-Until { Has $s.W 'You are offline' } 40
    Check 'H1 the page says "You are offline, and no list is saved on this PC yet."' $ok ((Row-Texts $s.W | Where-Object { $_ -match 'offline|reach|valid' }) -join ' | ')
    Check 'H1 a "Try again" button is offered, no module row is shown' ((Has $s.W 'Try again' $script:CT::Button) -and -not (Has $s.W 'Install Recorder' $script:CT::Button))
    Shot $s.W 'h1-offline-no-list'
    Check 'H1 only the registry address was tried: no install folder content beyond the cache folder' ((Names $s.Inst) -notcontains 'ready')
    Stop-MyApps

    # ============================================================ H2 offline with a saved list
    Say '-- H2 offline with a saved list'
    $s = Start-Scenario 'h2-offline-cache'
    [void](Click-Button $s.W 'Get the module list')
    Check 'H2 first the list is fetched from GitHub (no proxy)' (Wait-Until { Has $s.W 'Install Recorder' $script:CT::Button } 30)
    Check 'H2 the cache is on disk, next to the modules folder (not in LOCALAPPDATA)' (Test-Path (Join-Path $s.Inst 'registry\registry.json'))
    $meta1 = Get-Content (Join-Path $s.Inst 'registry\registry.meta.json') -Raw
    Say "  registry.meta.json: $($meta1 -replace '\s+', ' ')"
    Stop-MyApps
    Start-Sleep -Seconds 1
    $s = Resume-Scenario 'h2-offline-cache' @() $DeadProxy
    Check 'H2 restarted with the dead proxy: the saved list is already on the page' (Has $s.W 'Install Recorder' $script:CT::Button)
    Check 'H2 the page says it is the saved list' (Has $s.W 'Module list saved on this PC')
    [void](Click-Button $s.W 'Check for updates')
    $ok = Wait-Until { Has $s.W 'You are offline. Showing the list saved on' } 40
    Check 'H2 Check for updates: "You are offline. Showing the list saved on <date>."' $ok ((Row-Texts $s.W | Where-Object { $_ -match 'offline' }) -join ' | ')
    Check 'H2 the four modules are still listed from the saved copy' ((Has $s.W 'Install PredatorSense' $script:CT::Button) -and (Has $s.W 'Install Recorder' $script:CT::Button) -and (Has $s.W 'Install Steam access' $script:CT::Button) -and (Has $s.W 'Install XTU profiles' $script:CT::Button))
    Shot $s.W 'h2-offline-saved-list'
    [void](Click-Button $s.W 'Install Recorder')
    $ok = Wait-Until { Has $s.W 'Failed.' } 40
    $failText = (Row-Texts $s.W | Where-Object { $_ -match '^Failed\.' }) -join ' | '
    Check 'H2 installing while offline fails honestly in the row' $ok $failText
    Check 'H2 the failed install left nothing: no pending change, no ready\, no download' ((PendingCount $s.Inst) -eq 0 -and (Names (Join-Path $s.Inst 'ready')).Count -eq 0 -and (Names (Join-Path $s.Inst 'downloads')).Count -eq 0 -and (Names $s.Mods).Count -eq 0)
    Check 'H2 the row offers Try again' (Has $s.W 'Install Recorder' $script:CT::Button)
    Shot $s.W 'h2-offline-install-failed'
    Stop-MyApps

    # ============================================================ H3, H4 local registry of damaged copies of the REAL zips
    Say '-- H3/H4 damaged downloads and cancel: a local registry (the app''s own --registry switch, scratch folders only) listing damaged copies of the real release zips'
    $real = Invoke-RestMethod 'https://raw.githubusercontent.com/rigbay-app/registry/main/registry.json' -UseBasicParsing
    $zdir = Join-Path $Work 'zips'
    New-Item -ItemType Directory -Force $zdir | Out-Null
    $good = @{}
    foreach ($id in 'recorder', 'predatorsense') {
        $v = ($real.modules | Where-Object id -eq $id).versions[-1]
        $f = Join-Path $zdir "$id-good.zip"
        Invoke-WebRequest $v.zip -OutFile $f -UseBasicParsing
        $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
        Check "the real $id zip downloaded for the fixtures has the registry's SHA-256" ($h -eq $v.sha256) "$($v.size) bytes"
        $good[$id] = @{ Path = $f; Sha = $h; Size = (Get-Item $f).Length; Entry = ($real.modules | Where-Object id -eq $id) }
    }
    $rec = $good['recorder']
    $bytes = [IO.File]::ReadAllBytes($rec.Path)
    # 1 one byte flipped in the middle (same size, other SHA-256)
    $flip = $bytes.Clone(); $flip[[int]($flip.Length / 2)] = $flip[[int]($flip.Length / 2)] -bxor 0xFF
    [IO.File]::WriteAllBytes((Join-Path $zdir 'flipped.zip'), $flip)
    # 2 cut in half (the list still says the full size and SHA-256)
    [IO.File]::WriteAllBytes((Join-Path $zdir 'cut.zip'), $bytes[0..([int]($bytes.Length / 2) - 1)])
    # 3 not a zip at all, but the list's hash and size are those of this file
    $junk = New-Object byte[] 4096; (New-Object Random 7).NextBytes($junk)
    [IO.File]::WriteAllBytes((Join-Path $zdir 'junk.zip'), $junk)
    $junkSha = (Get-FileHash (Join-Path $zdir 'junk.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    # 4 the real file, but the list says it is 10 bytes longer
    Copy-Item $rec.Path (Join-Path $zdir 'wrongsize.zip')

    function Entry([string]$Id, [string]$Name, $From, [string]$ZipFile, [string]$Sha, [long]$Size) {
        $v = $From.Entry.versions[-1]
        [ordered]@{
            id = $Id; name = $Name; description = $From.Entry.description; repo = $From.Entry.repo
            versions = @([ordered]@{ version = $v.version; hostApi = $v.hostApi; zip = ([Uri](Join-Path $zdir $ZipFile)).AbsoluteUri; sha256 = $Sha; size = $Size; released = $v.released; declares = $v.declares })
        }
    }
    $ps = $good['predatorsense']
    $mods = @(
        (Entry 'e2e-flipped' 'Recorder (one byte flipped)' $rec 'flipped.zip' $rec.Sha $rec.Size),
        (Entry 'e2e-cut' 'Recorder (cut in half)' $rec 'cut.zip' $rec.Sha $rec.Size),
        (Entry 'e2e-junk' 'Not a zip (right hash)' $rec 'junk.zip' $junkSha 4096),
        (Entry 'e2e-wrongsize' 'Recorder (list says 10 bytes more)' $rec 'wrongsize.zip' $rec.Sha ($rec.Size + 10)),
        (Entry 'predatorsense' 'PredatorSense (slow, for Cancel)' $ps 'predatorsense-good.zip' $ps.Sha $ps.Size)
    )
    $regFile = Join-Path $Work 'hook-registry.json'
    [IO.File]::WriteAllText($regFile, ([ordered]@{ schemaVersion = 1; modules = $mods } | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Copy-Item $regFile (Join-Path $Out 'hooks-registry.json')

    $s = Start-Scenario 'h3-damaged' @('--registry', $regFile, '--slow-download', '60')
    Check 'H3 window up' ($null -ne $s.W)
    [void](Click-Button $s.W 'Get the module list')
    Check 'H3 the local list shows the five fixture rows' (Wait-Until { Has $s.W 'Install Recorder (one byte flipped)' $script:CT::Button } 20)
    Shot $s.W 'h3-list'
    $cases = @(
        @{ Name = 'Recorder (one byte flipped)'; Want = 'hash|match|differ|SHA' ; Label = 'one byte flipped, same size' },
        @{ Name = 'Recorder (cut in half)'; Want = 'incomplete|short|size|bytes|download' ; Label = 'cut in half' },
        @{ Name = 'Not a zip (right hash)'; Want = 'zip' ; Label = 'not a zip' },
        @{ Name = 'Recorder (list says 10 bytes more)'; Want = 'size|incomplete|bytes|download' ; Label = 'list size 10 bytes more than the file' }
    )
    $n = 0
    foreach ($c in $cases) {
        $n++
        [void](Click-Button $s.W ('Install ' + $c.Name))
        $ok = Wait-Until { (@(Row-Texts $s.W | Where-Object { $_ -like 'Failed.*' }).Count) -ge $n } 40
        $failTexts = @(Row-Texts $s.W | Where-Object { $_ -like 'Failed.*' })
        $mine = if ($failTexts.Count -ge $n) { $failTexts[$n - 1] } else { '' }
        Say "  [$($c.Label)] row text: $($failTexts -join ' || ')"
        Check "H3 $($c.Label): the install fails with a reason in the row" ($ok -and $mine -match $c.Want) $mine
        Start-Sleep -Milliseconds 500
    }
    Shot $s.W 'h3-all-failed'
    Check 'H3 nothing was queued, parked or unpacked by any of the four' ((PendingCount $s.Inst) -eq 0 -and (Names (Join-Path $s.Inst 'ready')).Count -eq 0 -and (Names (Join-Path $s.Inst 'staging')).Count -eq 0 -and (Names (Join-Path $s.Inst 'downloads')).Count -eq 0 -and (Names $s.Mods).Count -eq 0) ('modules: ' + ((Names $s.Mods) -join ','))
    Check 'H3 no restart bar was raised by a failed install' (-not (Has $s.W 'Restart now' $script:CT::Button))
    $log = Get-Content (Join-Path $s.Dir 'data\logs\rigbay.log') -Raw
    Check 'H3 the log has the exact English detail of each failure' (([regex]::Matches($log, 'install e2e-[a-z]+ .*(failed|refused|mismatch|incomplete|not a zip|size)', 'IgnoreCase')).Count -ge 3) (([regex]::Matches($log, '(?m)^.*install e2e-.*$') | ForEach-Object { $_.Value.Substring(0, [Math]::Min(200, $_.Value.Length)) }) -join ' || ')

    # H4 cancel (a slow download: 16 KB slices, 1.5 s each)
    Say '-- H4 cancel'
    Stop-MyApps
    Start-Sleep -Seconds 1
    $s = Resume-Scenario 'h3-damaged' @('--registry', $regFile, '--slow-download', '1500')
    [void](Click-Button $s.W 'Check for updates')
    Check 'H4 list is there' (Wait-Until { Has $s.W 'Install PredatorSense (slow, for Cancel)' $script:CT::Button } 20)
    [void](Click-Button $s.W 'Install PredatorSense (slow, for Cancel)')
    $ok = Wait-Until { Has $s.W 'Downloading' } 15
    Check 'H4 progress is shown: "Downloading x / y" with a Cancel button' ($ok -and (Has $s.W 'Cancel PredatorSense (slow, for Cancel)' $script:CT::Button)) ((Row-Texts $s.W | Where-Object { $_ -match 'Downloading' }) -join ' | ')
    Start-Sleep -Seconds 5
    $bar = Find-El $s.W 'PredatorSense (slow, for Cancel)' $script:CT::ProgressBar -TimeoutSec 2 -Quiet
    $pv = $null; if ($bar) { try { $pv = $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value } catch { } }
    Say "  progress bar value after 5 s: $pv"
    Check 'H4 the bar moves (some percent after 5 s) and the byte counter grew' ($null -ne $pv -and $pv -gt 0 -and $pv -lt 100) "value $pv"
    Shot $s.W 'h4-downloading'
    $downloadsDuring = (Names (Join-Path $s.Inst 'downloads'))
    Check 'H4 while it runs the partial file is in downloads\' ($downloadsDuring.Count -ge 1) ($downloadsDuring -join ',')
    [void](Click-Button $s.W 'Cancel PredatorSense (slow, for Cancel)')
    $ok = Wait-Until { Has $s.W 'Install PredatorSense (slow, for Cancel)' $script:CT::Button } 20
    Check 'H4 Cancel: the Install button is back' $ok
    Start-Sleep -Seconds 1
    $rt = Row-Texts $s.W
    Check 'H4 a cancel is not shown as a failure, and no restart bar' (-not ($rt | Where-Object { $_ -like 'Failed.*' } | Where-Object { $_ -match 'cancel' }) -and -not (Has $s.W 'Restart now' $script:CT::Button)) (($rt | Where-Object { $_ -like 'Failed.*' }) -join ' || ')
    Check 'H4 the partial file is deleted, nothing queued or parked' ((Names (Join-Path $s.Inst 'downloads')).Count -eq 0 -and (PendingCount $s.Inst) -eq 0 -and (Names (Join-Path $s.Inst 'ready')).Count -eq 0 -and (Names (Join-Path $s.Inst 'staging')).Count -eq 0 -and (Names $s.Mods).Count -eq 0)
    Shot $s.W 'h4-after-cancel'
    Stop-MyApps

    # ============================================================ H6 the real ProgramData layout: a parent folder called Rigbay
    Say '-- H6 admin-only folders, with the modules folder inside a parent named Rigbay (the layout of C:\ProgramData\Rigbay\modules)'
    $s = Start-Scenario 'h6-acl' @() @{} 'Rigbay\modules'
    [void](Click-Button $s.W 'Get the module list')
    Check 'H6 list fetched' (Wait-Until { Has $s.W 'Install Recorder' $script:CT::Button } 30)
    [void](Click-Button $s.W 'Install Recorder')
    Check 'H6 install queued' (Wait-Until { Has $s.W 'Restart now' $script:CT::Button } 30)
    Stop-MyApps
    Start-Sleep -Seconds 1
    $parent = Split-Path $s.Mods -Parent
    $targets = @($parent, $s.Mods, $s.Inst, (Join-Path $s.Inst 'pending.json'), (Join-Path $s.Inst 'registry\registry.json'), (Join-Path $s.Inst 'ready\recorder\module.json'))
    foreach ($f in $targets) {
        $acl = Get-Acl -LiteralPath $f
        $who = @($acl.Access | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
        $inh = @($acl.Access | Where-Object { $_.IsInherited }).Count
        $rel = $f.Substring($Work.Length + 1)
        $isLeaf = -not (Test-Path -LiteralPath $f -PathType Container)
        $okWho = ($who.Count -eq 2 -and $who -contains 'NT AUTHORITY\SYSTEM' -and $who -contains 'BUILTIN\Administrators')
        $okOwner = ($acl.Owner -in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')
        Check "H6 ACL of ${rel}: only Administrators and SYSTEM, owned by one of them, not inherited from the parent" ($okWho -and $okOwner -and ($acl.AreAccessRulesProtected -or $isLeaf)) ("who=$($who -join ',') owner=$($acl.Owner) protected=$($acl.AreAccessRulesProtected) inherited=$inh")
    }
    Say "  (folders kept for the non-elevated check of run.ps1: $parent)"

    # ============================================================ H5 a list that is not valid
    Say '-- H5 a list that is not valid'
    $bad = Join-Path $Work 'bad-registry.json'
    [IO.File]::WriteAllText($bad, '{ "schemaVersion": 1, "modules": [ { "id": "Bad Id", "name": "x" } ] }', (New-Object Text.UTF8Encoding($false)))
    $s = Start-Scenario 'h5-invalid' @('--registry', $bad)
    [void](Click-Button $s.W 'Get the module list')
    $ok = Wait-Until { Has $s.W 'not valid' } 20
    Check 'H5 an invalid list is refused as a whole and the page says so' $ok ((Row-Texts $s.W | Where-Object { $_ -match 'valid' }) -join ' | ')
    Shot $s.W 'h5-invalid-list'
    Stop-MyApps
}
catch {
    Check 'hooks ended without an exception' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
    Stop-MyApps
    Start-Sleep -Seconds 1
    foreach ($d in 'h1-offline-none', 'h2-offline-cache', 'h3-damaged', 'h5-invalid') { Copy-Item -ErrorAction SilentlyContinue (Join-Path $Work "$d\data\logs\rigbay.log") (Join-Path $Out "hooks-$d.log") }
    Check 'no process of this run is left' (@(Get-MyApps).Count -eq 0)
    Say "== E2E1 hooks: $($script:Pass) passed, $($script:Fail) failed"
}
exit ([int]($script:Fail -gt 0))
