# Runs the in-app self-test inside an elevated process against the demo modules (out\modules-test) and prints the report.
# Exit code: 0 when every check passed.
# The install checks run against a SCRATCH copy of the demo modules (out\selftest-install) and a local registry built from them
# (tools\make-local-registry.ps1): nothing is installed into out\modules-test, the real %ProgramData%\Rigbay\modules or anywhere on GitHub.
# The installer makes the scratch folders admin-only, so they are removed at the end by tools\remove-elevated.ps1 (UAC auto-elevates).
param([string]$Report = (Join-Path $PSScriptRoot 'out\selftest.txt'), [string]$AppDir)   # -AppDir: the app\ folder of an extracted release zip
$ErrorActionPreference = 'Stop'
$data = Join-Path $PSScriptRoot 'out\data-selftest'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
Remove-Item $Report -ErrorAction SilentlyContinue
$src = Join-Path $PSScriptRoot 'out\modules-test'
$scratch = Join-Path $PSScriptRoot 'out\selftest-install'
$ids = @(Get-ChildItem $src -Directory | ForEach-Object Name)
& "$PSScriptRoot\make-local-registry.ps1" -Source $src -Out $scratch -Installed $ids -InstallableId demo-extra -InstallableName 'Extra lighting (demo)' | Out-Null
try {
    $appArgs = @{}; if ($AppDir) { $appArgs.AppDir = (Resolve-Path $AppDir).Path }
    $code = & "$PSScriptRoot\run-elevated.ps1" @appArgs -Wait -Data $data -Modules (Join-Path $scratch 'modules') --registry (Join-Path $scratch 'registry.json') --slow-download 400 --selftest --selftest-out $Report --exit-after 500 | Select-Object -Last 1
}
finally {
    & "$PSScriptRoot\remove-elevated.ps1" $scratch | Out-Null
}
if (-not (Test-Path $Report)) { Write-Host "no report written (exit code $code)"; exit 1 }
Get-Content $Report -Encoding utf8
$fails = (Get-Content $Report -Encoding utf8 | Select-String 'SELFTEST FAIL').Count
"exit code of the app: $code; failed checks: $fails"
exit ([int]($fails -gt 0 -or $code -ne 0))
