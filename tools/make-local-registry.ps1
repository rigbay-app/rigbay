# Builds a SCRATCH install scenario from the demo modules, for rendering the Modules page and for the self-test. It never touches the real
# %ProgramData%\Rigbay\modules and never reaches the network:
#   <Out>\modules\        the modules folder the app is started with (--modules): a copy of the modules in -Installed
#   <Out>\registry.json   a local registry (the app reads it with --registry <file>, which only works with scratch --data and --modules)
#   <Out>\zips\*.zip      the release assets the registry points to (file: addresses)
# Scenario (what the Modules page shows):
#   installed + newer release     demo-fan        -> "Update to ..."
#   installed + same release      demo-devices    -> "Installed"
#   offered, installable          demo-lighting   -> Install
#   offered, wrong SHA-256        demo-tampered   -> Install fails with "hash mismatch" (a copy of demo-lighting with a bad hash in the registry)
#   offered, other host API       demo-next       -> "Needs host API >=0.2 <0.3"
# Usage: .\make-local-registry.ps1 [-Source tools\out\modules] [-Out tools\out\local-registry]
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'out\modules'),
    [string]$Out = (Join-Path $PSScriptRoot 'out\local-registry'),
    [string[]]$Installed = @('demo-fan', 'demo-devices', 'demo-future', 'demo-broken'),
    # empty: zips are addressed as file: URLs. Otherwise a base such as http://127.0.0.1:18765/zips/ (toolsserve-registry.ps1 serves it)
    [string]$BaseUrl = '',
    # the id (and name) of the module the registry offers for installing; the self-test installs everything else, so it offers a new id
    [string]$InstallableId = 'demo-lighting',
    [string]$InstallableName = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path $Out) {
    try { Remove-Item $Out -Recurse -Force -ErrorAction Stop }
    catch { & "$PSScriptRoot\remove-elevated.ps1" $Out | Out-Null }   # an earlier run made folders in it admin-only
}
New-Item -ItemType Directory -Force (Join-Path $Out 'modules'), (Join-Path $Out 'zips'), (Join-Path $Out 'work') | Out-Null
$work = Join-Path $Out 'work'

foreach ($id in $Installed) { Copy-Item (Join-Path $Source $id) (Join-Path $Out "modules\$id") -Recurse }

function Zip-Module([string]$id, [string]$version, [string]$asId, [string]$hostApi, [string]$name = '') {
    $stage = Join-Path $work "$asId-$version"
    Copy-Item (Join-Path $Source $id) $stage -Recurse
    $mj = Join-Path $stage 'module.json'
    $json = Get-Content $mj -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.id = $asId
    $json.version = $version
    if ($hostApi) { $json.hostApi = $hostApi }
    if ($name) { $json.name = $name }
    [IO.File]::WriteAllText($mj, ($json | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $zip = Join-Path $Out "zips\$asId-$version.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    return $zip
}

function Entry([string]$zip, [string]$moduleFolder, [string]$asId, [string]$version, [string]$hostApi, [string]$sha) {
    $m = Get-Content (Join-Path $moduleFolder 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $req = $m.requires
    $declares = [ordered]@{
        admin        = [bool]($req -and $req.admin)
        network      = @(if ($req -and $req.network) { $req.network })
        registry     = @(if ($req -and $req.registry) { $req.registry })
        services     = @(if ($req -and $req.services) { $req.services })
        outsideFiles = @(if ($m.probePaths) { $m.probePaths })
    }
    [ordered]@{
        id          = $asId
        name        = $m.name
        description = $m.description
        repo        = "https://github.com/rigbay-app/module-$asId"
        versions    = @([ordered]@{
            version  = $version
            hostApi  = $hostApi
            zip      = $(if ($BaseUrl) { $BaseUrl.TrimEnd('/') + '/' + (Split-Path $zip -Leaf) } else { ([Uri]$zip).AbsoluteUri })
            sha256   = $(if ($sha) { $sha } else { (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() })
            size     = (Get-Item $zip).Length
            released = '2026-11-01'
            declares = $declares
        })
    }
}

$modules = @()
# installed + newer release
$fan = Get-Content (Join-Path $Source 'demo-fan\module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fanNew = ([version]$fan.version); $fanNewText = '{0}.{1}.{2}' -f $fanNew.Major, ($fanNew.Minor + 1), 0
$zip = Zip-Module 'demo-fan' $fanNewText 'demo-fan' $null
$modules += Entry $zip (Join-Path $work "demo-fan-$fanNewText") 'demo-fan' $fanNewText '>=0.1.2 <0.2' $null
# installed + same release
$dev = Get-Content (Join-Path $Source 'demo-devices\module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$zip = Zip-Module 'demo-devices' $dev.version 'demo-devices' $null
$modules += Entry $zip (Join-Path $work "demo-devices-$($dev.version)") 'demo-devices' $dev.version '>=0.1 <0.2' $null
# installable
$lit = Get-Content (Join-Path $Source 'demo-lighting\module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$zip = Zip-Module 'demo-lighting' $lit.version $InstallableId $null $InstallableName
$modules += Entry $zip (Join-Path $work "$InstallableId-$($lit.version)") $InstallableId $lit.version '>=0.1 <0.2' $null
# wrong hash
$zip = Zip-Module 'demo-lighting' '0.1.0' 'demo-tampered' $null 'Keyboard lighting (bad hash)'
$modules += Entry $zip (Join-Path $work 'demo-tampered-0.1.0') 'demo-tampered' '0.1.0' '>=0.1 <0.2' ('0' * 64)
# other host API
$zip = Zip-Module 'demo-lighting' '1.0.0' 'demo-next' '>=0.2 <0.3' 'Keyboard lighting (next API)'
$modules += Entry $zip (Join-Path $work 'demo-next-1.0.0') 'demo-next' '1.0.0' '>=0.2 <0.3' $null

$registry = [ordered]@{ schemaVersion = 1; modules = $modules }
$file = Join-Path $Out 'registry.json'
[IO.File]::WriteAllText($file, ($registry | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Remove-Item $work -Recurse -Force
"registry: $file"
"modules:  $(Join-Path $Out 'modules')  ($($Installed -join ', '))"
