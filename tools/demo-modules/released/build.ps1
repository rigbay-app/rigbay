# Builds the four released modules (XTU profiles, PredatorSense, Steam access, Recorder) as they are in modules\, each on the fake machine of its
# own unit tests, and lays them out as module folders under -OutDir: the module's own module.json with only the entry changed to the demo type, its
# language files and icons, its DLL and the demo DLL. For rendering their pages with made-up data (tools\shots.ps1 'released-*' jobs, the UI review
# before/after pictures): no hardware read or write, no service, no Windows setting, no network, no user or machine name.
#   powershell -ExecutionPolicy Bypass -File tools\demo-modules\released\build.ps1 -OutDir tools\out\modules-released
param([Parameter(Mandatory)][string]$OutDir, [string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$sets = @(
    @{ dir = 'xtu';           proj = 'Rigbay.Released.Xtu.csproj';           manifest = 'modules\xtu\Rigbay.Module.Xtu\module.json';  lang = 'modules\xtu\Rigbay.Module.Xtu\lang';                      asm = 'Rigbay.Module.Xtu.Tests.dll';    type = 'Rigbay.Module.Xtu.Tests.ReleasedXtuDemo' },
    @{ dir = 'predatorsense'; proj = 'Rigbay.Released.PredatorSense.csproj'; manifest = 'modules\predatorsense\module.json';           lang = 'modules\predatorsense\src\Rigbay.PredatorSense\lang';     asm = 'Rigbay.PredatorSense.Tests.dll'; type = 'Rigbay.PredatorSense.Tests.ReleasedPredatorSenseDemo' },
    @{ dir = 'steambypass';   proj = 'Rigbay.Released.SteamBypass.csproj';   manifest = 'modules\steambypass\module.json';             lang = 'modules\steambypass\src\Rigbay.SteamBypass\lang';         asm = 'Rigbay.SteamBypass.Tests.dll';   type = 'Rigbay.SteamBypass.Tests.ReleasedSteamDemo' },
    @{ dir = 'recorder';      proj = 'Rigbay.Released.Recorder.csproj';      manifest = 'modules\recorder\module.json';                lang = 'modules\recorder\src\Rigbay.Recorder\lang';               asm = 'Rigbay.Recorder.Tests.dll';      type = 'Rigbay.Recorder.Tests.ReleasedRecorderDemo' }
)
foreach ($s in $sets) {
    $manifest = Join-Path $repo $s.manifest
    $json = Get-Content $manifest -Raw -Encoding utf8
    if ($json -notmatch '"id"\s*:\s*"([^"]+)"') { throw "no id in $manifest" }
    $target = Join-Path $OutDir $Matches[1]
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    & dotnet build (Join-Path $here (Join-Path $s.dir $s.proj)) -c $Config -o $target -v q --nologo -m:2 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "build of $($s.dir) failed" }
    # the module's own manifest with the demo as its entry point; everything else (id, name, version, group, requires, icon) as released
    $entry = '"entry": { "assembly": "' + $s.asm + '", "type": "' + $s.type + '" }'
    $json = [regex]::Replace($json, '"entry"\s*:\s*\{[^}]*\}', $entry)
    [IO.File]::WriteAllText((Join-Path $target 'module.json'), $json, (New-Object Text.UTF8Encoding $false))
    $langTarget = Join-Path $target 'lang'
    New-Item -ItemType Directory -Force $langTarget | Out-Null
    Copy-Item (Join-Path (Join-Path $repo $s.lang) '*.lang') $langTarget -Force
    Get-ChildItem (Split-Path $manifest) -File | Where-Object { $_.Name -like 'icon*' } | Copy-Item -Destination $target -Force
    Get-ChildItem $target -Filter 'Rigbay.Abstractions.*' | Remove-Item -Force
    Get-ChildItem $target -Filter '*.pdb' | Remove-Item -Force
    "{0}: {1}" -f $Matches[1], $target
}
