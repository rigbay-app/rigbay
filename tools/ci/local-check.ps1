# Everything CI does, on this machine, plus the in-app self-test that CI cannot run (it needs an interactive desktop and
# an elevated process). Run it before a pull request:
#   powershell -ExecutionPolicy Bypass -File tools\ci\local-check.ps1
#   -SkipSelfTest   leave out the elevated self-test (UAC auto-elevation or an admin session is needed for it)
#   -Personal       also scan for machine-specific strings (user profile name, machine name)
# Exit code 0 = every step passed.
param([switch]$SkipSelfTest, [switch]$Personal)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
$results = [ordered]@{}
function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name"
    $global:LASTEXITCODE = 0
    try { & $body; $ok = ($LASTEXITCODE -eq 0) } catch { Write-Host $_; $ok = $false }
    $results[$name] = $ok
}
try {
    Step 'build (warnings are errors)' { dotnet build Rigbay.sln --no-incremental -warnaserror --nologo }
    Step 'test' { dotnet test Rigbay.sln --no-build --nologo }
    Step 'langcheck (strict)' { & "$PSScriptRoot\langcheck.ps1" }
    Step 'scan' { if ($Personal) { & "$PSScriptRoot\scan.ps1" -Personal } else { & "$PSScriptRoot\scan.ps1" } }
    Step 'links' { & "$PSScriptRoot\check-links.ps1" }
    if (-not $SkipSelfTest) {
        Step 'build app + demo modules' { & "$root\tools\build.ps1" }
        Step 'in-app self-test (elevated)' { & "$root\tools\selftest.ps1" }
    }
}
finally { Pop-Location }
Write-Host "`n=== summary"
$results.GetEnumerator() | ForEach-Object { Write-Host ("{0,-6} {1}" -f $(if ($_.Value) { 'ok' } else { 'FAILED' }), $_.Key) }
if ($SkipSelfTest) { Write-Host 'skipped: in-app self-test' }
exit ([int]($results.Values -contains $false))
