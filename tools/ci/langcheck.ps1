# Strict LangCheck on every lang folder of the repository (the same list as docs/ABSTRACTIONS.md, "Language check").
# Used by CI and by tools\ci\local-check.ps1. Exit code: 0 when every folder is clean, 1 otherwise.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    dotnet build src/Rigbay.LangCheck -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { exit 1 }
    # lang folder, module id (none for the host), source folder
    $sets = @(
        @('src/Rigbay.Core/lang', $null, 'src/Rigbay.Core'),
        @('src/Rigbay.App/lang', $null, 'src/Rigbay.App'),
        @('samples/Rigbay.SampleModule/lang', 'sample', 'samples/Rigbay.SampleModule'),
        @('modules/predatorsense/src/Rigbay.PredatorSense/lang', 'predatorsense', 'modules/predatorsense/src/Rigbay.PredatorSense'),
        @('modules/recorder/src/Rigbay.Recorder/lang', 'recorder', 'modules/recorder/src/Rigbay.Recorder'),
        @('modules/steambypass/src/Rigbay.SteamBypass/lang', 'steam-bypass', 'modules/steambypass/src/Rigbay.SteamBypass'),
        @('modules/xtu/Rigbay.Module.Xtu/lang', 'xtu', 'modules/xtu/Rigbay.Module.Xtu'),
        @('modules/tunnel/src/Rigbay.Tunnel/lang', 'tunnel', 'modules/tunnel/src/Rigbay.Tunnel'),
        @('modules/gpu/src/Rigbay.Gpu/lang', 'gpu', 'modules/gpu/src/Rigbay.Gpu'),
        @('modules/powerrules/src/Rigbay.PowerRules/lang', 'power-rules', 'modules/powerrules/src/Rigbay.PowerRules'),
        @('modules/watch/src/Rigbay.Watch/lang', 'watch', 'modules/watch/src/Rigbay.Watch'),
        @('tools/demo-modules/modules/demo-devices/lang', 'demo-devices', 'tools/demo-modules'),
        @('tools/demo-modules/modules/demo-fan/lang', 'demo-fan', 'tools/demo-modules'),
        @('tools/demo-modules/modules/demo-lighting/lang', 'demo-lighting', 'tools/demo-modules'),
        @('tools/demo-modules/modules-fragile/demo-plan-owner/lang', 'demo-plan-owner', 'tools/demo-modules'),
        @('tools/demo-modules/modules-fragile/demo-plan-target/lang', 'demo-plan-target', 'tools/demo-modules')
    )
    # every folder with .lang files must be in the list above, so a new module cannot slip past the check
    $known = $sets | ForEach-Object { (Resolve-Path $_[0]).Path }
    $all = Get-ChildItem -Recurse -Filter *.lang -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|spike|out)[\\/]' } |
        ForEach-Object { $_.DirectoryName } | Sort-Object -Unique
    $failed = 0
    foreach ($d in $all) { if ($known -notcontains $d) { Write-Host "NOT CHECKED: $d has .lang files but is not listed in tools/ci/langcheck.ps1"; $failed++ } }
    foreach ($s in $sets) {
        $a = @("run", "--project", "src/Rigbay.LangCheck", "-c", "Release", "--no-build", "--", $s[0], '--source', $s[2], '--strict')
        if ($s[1]) { $a += @('--module', $s[1]) }
        Write-Host "== $($s[0])"
        dotnet @a
        if ($LASTEXITCODE -ne 0) { Write-Host "LANGCHECK FAILED: $($s[0]) (exit $LASTEXITCODE)"; $failed++ }
    }
    if ($failed) { Write-Host "langcheck: $failed problem(s)"; exit 1 }
    Write-Host "langcheck: $($sets.Count) folders clean"
}
finally { Pop-Location }
