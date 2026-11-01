# Builds Abstractions, the sample modules and the host into spike\out\.
#   out\host-fd   host with the shared Windows App Runtime (framework-dependent)
#   out\host-sc   host with the Windows App SDK runtime bundled (self-contained)
# Each has a modules\ folder next to the exe. Usage: .\build.ps1 [-WinAppSdk 2.6.4] [-Only fd|sc]
param(
    [string]$Config = 'Release',
    [string]$WinAppSdk = '',
    [ValidateSet('both', 'fd', 'sc')][string]$Only = 'both'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'out'
$extra = @()
if ($WinAppSdk) { $extra += "-p:WinAppSdkVersion=$WinAppSdk" }

function Run($a) { $ErrorActionPreference = 'Continue'; & dotnet @a 2>&1 | ForEach-Object { "$_" } | Write-Host; if ($LASTEXITCODE -ne 0) { throw "dotnet $($a -join ' ') failed ($LASTEXITCODE)" } }

if (Test-Path $out) { Get-ChildItem $out | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# modules (shared by both host variants)
$modules = 'Code', 'Xaml', 'Loose', 'Broken', 'Rows'
foreach ($m in $modules) {
    Run (@('build', "$src\Rigbay.Module.$m", '-c', $Config, '-o', "$out\_modules\Rigbay.Module.$m", '-v', 'q', '--nologo') + $extra)
}

function PublishHost($name, $selfContained) {
    $o = "$out\$name"
    $sc = if ($selfContained) { 'true' } else { 'false' }
    Run (@('publish', "$src\Rigbay.Host", '-c', $Config, '-r', 'win-x64', '--self-contained', $sc,
           "-p:WindowsAppSDKSelfContained=$sc", '-o', $o, '-v', 'q', '--nologo') + $extra)
    foreach ($m in $modules) {
        Copy-Item "$out\_modules\Rigbay.Module.$m" "$o\modules\Rigbay.Module.$m" -Recurse
    }
    $size = (Get-ChildItem $o -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    "{0}: {1:N1} MB, {2} files" -f $name, $size, (Get-ChildItem $o -Recurse -File).Count
}

if ($Only -in 'both', 'fd') { PublishHost 'host-fd' $false }
if ($Only -in 'both', 'sc') { PublishHost 'host-sc' $true }
