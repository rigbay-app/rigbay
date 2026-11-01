# E2E1 orchestrator (run from a normal PowerShell; the driver scripts are started elevated, UAC auto-elevates for this admin account).
#   1. downloads the RELEASED app zip from GitHub, checks its SHA-256 against the one GitHub reports for the asset, extracts it to tools\out\e2e\app
#   2. flow.ps1  -Theme dark / light : the first-hour flow against the real registry and real module releases, in scratch folders
#   3. hooks.ps1                     : offline fallback, corrupted downloads, cancel, an invalid list, admin-only folders (scratch folders,
#                                      the app's own test switches); then acl-user-check.ps1 from THIS (non-elevated) shell
# Everything lives under tools\out\e2e (git-ignored). Evidence (PNGs, reports, CSV, logs) goes to tools\out\e2e\<EvidenceName>.
# Usage: .\run.ps1 [-Only flow-dark,flow-light,hooks] [-Tag v0.1.1] [-AppDir <dir with Rigbay.App.exe>] [-EvidenceName evidence]
#   -AppDir runs the same checks against another build (for example tools\out\app after a fix) instead of the released zip.
[CmdletBinding()]
param(
    [string[]]$Only = @('flow-dark', 'flow-light', 'hooks'),
    [string]$Tag = 'v0.1.1',
    [string]$Repo = 'rigbay-app/rigbay',
    [string]$AppDir,
    [string]$EvidenceName = 'evidence'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\out\e2e'))
$ev = Join-Path $root $EvidenceName
New-Item -ItemType Directory -Force $root, $ev | Out-Null
$invoke = Join-Path $PSScriptRoot 'invoke-elevated.ps1'

if (-not $AppDir) {
    # ---- the released zip, verified, extracted
    $dl = Join-Path $root 'dl'
    $zip = Join-Path $dl "Rigbay-$($Tag.TrimStart('v'))-win-x64.zip"
    if (-not (Test-Path $zip)) {
        New-Item -ItemType Directory -Force $dl | Out-Null
        Invoke-WebRequest "https://github.com/$Repo/releases/download/$Tag/$([IO.Path]::GetFileName($zip))" -OutFile $zip -UseBasicParsing
    }
    $want = ((gh release view $Tag --repo $Repo --json assets --jq '.assets[0].digest') -replace '^sha256:', '').Trim()
    $have = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "released zip: $zip"
    "sha256 of the file : $have"
    "sha256 GitHub says : $want"
    if ($have -ne $want) { throw 'the downloaded zip does not match the release digest' }
    $AppDir = Join-Path $root 'app'
    if (-not (Test-Path (Join-Path $AppDir 'Rigbay.App.exe'))) {
        $tmp = Join-Path $root 'zip'
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }
        Expand-Archive $zip $tmp
        Move-Item (Join-Path $tmp 'app') $AppDir
    }
}
$AppDir = [IO.Path]::GetFullPath($AppDir)
"app: $AppDir (ProductVersion $((Get-Item (Join-Path $AppDir 'Rigbay.App.exe')).VersionInfo.ProductVersion))"

$rc = 0
foreach ($theme in 'dark', 'light') {
    if ($Only -contains "flow-$theme") {
        & $invoke -File (Join-Path $PSScriptRoot 'flow.ps1') -Arguments '-Root', $root, '-Theme', $theme, '-Out', $ev, '-AppDir', $AppDir -TimeoutSec 600
        if ($LASTEXITCODE) { $rc = 1 }
    }
}
if ($Only -contains 'hooks') {
    & $invoke -File (Join-Path $PSScriptRoot 'hooks.ps1') -Arguments '-Root', $root, '-Out', $ev, '-AppDir', $AppDir -TimeoutSec 900
    if ($LASTEXITCODE) { $rc = 1 }
    & (Join-Path $PSScriptRoot 'acl-user-check.ps1') -Root $root -Out $ev
    if ($LASTEXITCODE) { $rc = 1 }
    # the admin-only scratch folders can only be removed by an elevated process
    & (Join-Path $PSScriptRoot '..\remove-elevated.ps1') (Join-Path $root 'hooks') | Out-Null
}
"evidence: $ev"
exit $rc
