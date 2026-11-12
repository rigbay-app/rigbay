# Lays out the demo build of the Remote tunnel module as a module folder (<OutDir>\tunnel) for screenshots and for looking at the page in the real
# app without WireGuard or a real tunnel. The demo is the real module on a made-up machine; it changes nothing.
#   powershell -ExecutionPolicy Bypass -File modules\tunnel\tools\demo\build-demo.ps1 -OutDir tools\out\modules-tunnel-demo
param([Parameter(Mandatory)][string]$OutDir, [string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$module = (Resolve-Path (Join-Path $here '..\..')).Path
$target = Join-Path $OutDir 'tunnel'
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
& dotnet build (Join-Path $here 'Rigbay.Tunnel.Demo.csproj') -c $Config -o $target -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'demo build failed' }
Copy-Item (Join-Path $here 'module.json') (Join-Path $target 'module.json') -Force   # the demo's entry point, not the real one
Get-ChildItem $target -Filter 'Rigbay.Abstractions.*' | Remove-Item -Force
Get-ChildItem $target -Filter '*.pdb' | Remove-Item -Force
"demo module laid out in $target"
