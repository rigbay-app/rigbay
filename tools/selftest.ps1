# Runs the in-app self-test in an elevated process against the demo modules (out\modules-test) and prints the report.
# Exit code: 0 when every check passed.
param([string]$Report = (Join-Path $PSScriptRoot 'out\selftest.txt'))
$ErrorActionPreference = 'Stop'
$data = Join-Path $PSScriptRoot 'out\data-selftest'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
Remove-Item $Report -ErrorAction SilentlyContinue
$code = & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules (Join-Path $PSScriptRoot 'out\modules-test') --selftest --selftest-out $Report --exit-after 500 | Select-Object -Last 1
if (-not (Test-Path $Report)) { Write-Host "no report written (exit code $code)"; exit 1 }
Get-Content $Report -Encoding utf8
$fails = (Get-Content $Report -Encoding utf8 | Select-String 'SELFTEST FAIL').Count
"exit code of the app: $code; failed checks: $fails"
exit ([int]($fails -gt 0 -or $code -ne 0))
