# Lays out the Watch module and its demo publisher as module folders under <OutDir> (watch, watch-demo-feed), for looking at the page in the real app
# without any sensor. Nothing here touches the machine.
#   powershell -ExecutionPolicy Bypass -File modules\watch\tools\demo\build-demo.ps1 -OutDir tools\out\modules-watch
param([Parameter(Mandatory)][string]$OutDir, [string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$module = (Resolve-Path (Join-Path $here '..\..')).Path
function Lay($project, $target) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    & dotnet build $project -c $Config -o $target -v q --nologo -m:2 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
    Get-ChildItem $target -Filter 'Rigbay.Abstractions.*' | Remove-Item -Force
    Get-ChildItem $target -Filter '*.pdb' | Remove-Item -Force
}
Lay (Join-Path $module 'src\Rigbay.Watch\Rigbay.Watch.csproj') (Join-Path $OutDir 'watch')
Lay (Join-Path $here 'Rigbay.Watch.Demo.csproj') (Join-Path $OutDir 'watch-demo-feed')
Copy-Item (Join-Path $here 'module.json') (Join-Path $OutDir 'watch-demo-feed\module.json') -Force   # the demo's entry point
"laid out: " + ((Get-ChildItem $OutDir -Directory | ForEach-Object Name) -join ', ')
