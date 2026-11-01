# Builds the app and the demo modules into tools\out:
#   out\app            Rigbay.App.exe (framework-dependent: needs the Windows App Runtime 1.8 and .NET 10, both installed here)
#   out\modules        the demo modules (one folder each); used by run-elevated.ps1 and shots.ps1
#   out\modules-test   the same plus three modules that misbehave on purpose; used by selftest.ps1
# Usage: .\build.ps1 [-Config Release] [-SelfContained] [-SkipDemo]
#   -SelfContained bundles the Windows App SDK and .NET (about 220 MB). It is NOT the main path; docs\APP.md says what was tested.
param(
    [string]$Config = 'Release',
    [switch]$SelfContained,
    [switch]$SkipDemo
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$app = if ($SelfContained) { Join-Path $out 'app-sc' } else { Join-Path $out 'app' }
function Run($a) { $ErrorActionPreference = 'Continue'; & dotnet @a 2>&1 | ForEach-Object { "$_" } | Write-Host; if ($LASTEXITCODE -ne 0) { throw "dotnet $($a -join ' ') failed ($LASTEXITCODE)" } }

New-Item -ItemType Directory -Force $out | Out-Null
$sc = if ($SelfContained) { 'true' } else { 'false' }
Run @('publish', "$repo\src\Rigbay.App\Rigbay.App.csproj", '-c', $Config, '-r', 'win-x64', "--self-contained:$sc", "-p:WindowsAppSDKSelfContained=$sc", '-o', $app, '-v', 'q', '--nologo')
$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"app: {0:N1} MB, {1} files -> {2}" -f $size, (Get-ChildItem $app -Recurse -File).Count, $app

if (-not $SkipDemo) { & "$PSScriptRoot\build-demo.ps1" -Config $Config }
