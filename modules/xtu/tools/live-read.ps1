# Read-only live check of the XTU module in the real Rigbay app (elevated, with its own data folder).
# NOTHING IS WRITTEN to XTU: the data folder gets a `no-write.flag`, which makes the module refuse every write before it asks, and the
# script never starts an apply. It builds the module, lays it out next to a published app, enables it, prints
# `rigbay xtu status` (and the old app's `--status` when -OldApp is given) and renders the module page of the app's own window
# (PrintWindow of the app's HWND, never the screen) in dark and light.
#
# Usage:  .\live-read.ps1 -AppDir <published Rigbay.App folder> [-Work <folder>] [-OldApp <XtuProfileSwitcher.exe>] [-NoShots]
# Publish the app first (does not touch the repo):
#   dotnet publish src\Rigbay.App\Rigbay.App.csproj -c Release -r win-x64 --self-contained:false -p:WindowsAppSDKSelfContained=false -o <AppDir>
# An elevated process cannot be stopped from a normal shell; every process here is short-lived and exits by itself (--exit-after).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDir,
    [string]$Work = (Join-Path $env:TEMP 'xtu-live'),
    [string]$OldApp = '',
    [switch]$NoShots
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = Join-Path $AppDir 'Rigbay.App.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe (publish the app first, see the header)" }

$modules = Join-Path $Work 'modules'
$data = Join-Path $Work 'data'
$shots = Join-Path $Work 'shots'
New-Item -ItemType Directory -Force (Join-Path $modules 'xtu'), (Join-Path $data 'modules\xtu'), $shots | Out-Null

# 1. build and lay out the module (no Intel file ends up in the folder)
& dotnet build (Join-Path $repo 'modules\xtu\Rigbay.Module.Xtu\Rigbay.Module.Xtu.csproj') -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'module build failed' }
$out = Join-Path $repo 'modules\xtu\Rigbay.Module.Xtu\bin\Release\net10.0'
Copy-Item "$out\*" (Join-Path $modules 'xtu') -Recurse -Force
$intel = Get-ChildItem (Join-Path $modules 'xtu') -Recurse -Include 'XtuInterface.dll', 'XtuCommon.dll'
if ($intel) { throw 'an Intel file was copied next to the module: ' + $intel.FullName }

# 2. the write latch
Set-Content -Path (Join-Path $data 'modules\xtu\no-write.flag') -Value 'read-only live check'

function Invoke-Elevated([string]$File, [string[]]$ArgList, [string]$Dir) {
    $q = $ArgList | ForEach-Object { if ($_ -match '[\s;]') { '"' + $_ + '"' } else { $_ } }
    $p = Start-Process -FilePath $File -Verb RunAs -PassThru -Wait -WorkingDirectory $Dir -ArgumentList $q
    $p.ExitCode
}
function Invoke-App([string[]]$AppArgs) {
    Invoke-Elevated $exe (@('--data', $data, '--modules', $modules) + $AppArgs) $AppDir
}

# 3. enable the module (the choice is kept in the data folder), then ask it
'enable:'; Invoke-App @('--script', 'enable:xtu;wait:4000', '--exit-after', '300', '--allow-multi') | Out-Null
foreach ($verb in 'status', 'list') {
    $f = Join-Path $Work "$verb.txt"; Remove-Item $f -ErrorAction SilentlyContinue
    $code = Invoke-App @('--out', $f, 'xtu', $verb)
    "== rigbay xtu $verb (exit $code)"; if (Test-Path $f) { Get-Content $f }
}
if ($OldApp) {
    $f = Join-Path $Work 'old-status.txt'; Remove-Item $f -ErrorAction SilentlyContinue
    $code = Invoke-Elevated $OldApp @('--status', '--out', $f) (Split-Path $OldApp)
    "== xtu-switcher --status (exit $code)"; if (Test-Path $f) { Get-Content $f }
}

# 4. the module page, dark and light (readings need about 5 s after enabling to leave the warm-up)
if (-not $NoShots) {
    foreach ($theme in 'dark', 'light') {
        $png = Join-Path $shots "page-$theme.png"; Remove-Item $png -ErrorAction SilentlyContinue
        Invoke-App @('--theme', $theme, '--size', '1240x1100', '--script', 'enable:xtu;wait:7000;page:xtu;wait:300', '--shot', $png, '--exit-after', '300', '--allow-multi') | Out-Null
        "$theme page: $(Test-Path $png) -> $png"
    }
}
"log: $(Join-Path $data 'logs\rigbay.log')  (look for: writes are switched OFF)"
