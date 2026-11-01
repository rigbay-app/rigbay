# Builds tools\demo-modules and lays the result out as module folders (manifest, language files, one DLL each).
param([string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
$build = Join-Path $out '_demo-build'
function Run($a) { $ErrorActionPreference = 'Continue'; & dotnet @a 2>&1 | ForEach-Object { "$_" } | Write-Host; if ($LASTEXITCODE -ne 0) { throw "dotnet $($a -join ' ') failed ($LASTEXITCODE)" } }
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
Run @('build', "$PSScriptRoot\demo-modules\Rigbay.DemoModules.csproj", '-c', $Config, '-o', $build, '-v', 'q', '--nologo')
$dll = Join-Path $build 'Rigbay.DemoModules.dll'
if (-not (Test-Path $dll)) { throw "not built: $dll" }

function Lay($target, $sources) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($src in $sources) {
        foreach ($m in Get-ChildItem $src -Directory) {
            $dest = Join-Path $target $m.Name
            Copy-Item $m.FullName $dest -Recurse
            # the invalid-manifest module has no code on purpose; every other one gets the DLL
            if ($m.Name -ne 'demo-bad-manifest') { Copy-Item $dll $dest }
        }
    }
    "{0}: {1}" -f $target, ((Get-ChildItem $target -Directory | ForEach-Object Name) -join ', ')
}
Lay (Join-Path $out 'modules') @("$PSScriptRoot\demo-modules\modules")
Lay (Join-Path $out 'modules-test') @("$PSScriptRoot\demo-modules\modules", "$PSScriptRoot\demo-modules\modules-fragile")
# an empty modules folder for the "no modules installed" screenshot
New-Item -ItemType Directory -Force (Join-Path $out 'modules-empty') | Out-Null
