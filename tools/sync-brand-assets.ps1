# The app carries copies of some files from assets\brand (app icon, tray and high-contrast icons, the Dock mark path data, the
# neutral "unknown module" icon). The list is src\Rigbay.App\Assets\brand-files.txt. assets\brand is the single source.
#   tools\sync-brand-assets.ps1           copies the masters over the app's files
#   tools\sync-brand-assets.ps1 -Check    changes nothing; exit code 1 when a copy is missing or differs (build.ps1 runs this)
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$list = Join-Path $repo 'src\Rigbay.App\Assets\brand-files.txt'
$bad = @()
foreach ($line in Get-Content $list) {
    $line = $line.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    $src, $dst = $line.Split('|')
    $from = Join-Path $repo ('assets\brand\' + $src.Replace('/', '\'))
    $to = Join-Path $repo ('src\Rigbay.App\' + $dst.Replace('/', '\'))
    if (-not (Test-Path $from)) { $bad += "missing master: $src"; continue }
    $same = (Test-Path $to) -and ((Get-FileHash $from -Algorithm SHA256).Hash -eq (Get-FileHash $to -Algorithm SHA256).Hash)
    if ($same) { continue }
    if ($Check) { $bad += "differs from assets\brand: $dst" }
    else { New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null; Copy-Item $from $to -Force; "copied $dst" }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host "brand asset check failed: $_" }; Write-Host "run tools\sync-brand-assets.ps1"; exit 1 }
if ($Check) { "brand assets: all copies equal assets\brand" } else { "brand assets: in sync" }
exit 0
