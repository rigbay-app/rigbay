# Creates a new module in THIS repository (modules\<id>) from samples\Rigbay.SampleModule, with the id, namespace, class names, language keys and
# texts renamed. For contributors who work inside the monorepo. If you are writing a module of your own in its own repository, copy
# https://github.com/rigbay-app/module-template instead (docs\WRITING-A-MODULE.md).
#
#   powershell -ExecutionPolicy Bypass -File tools\new-module.ps1 -Id fan-tools -Name "Fan tools"
#   powershell -ExecutionPolicy Bypass -File tools\new-module.ps1 -Id fan-tools -Group cooling -Description "Sets my fans." -Build
#
# The layout is the one tools\extract-module.ps1 expects (docs\MODULE-REPOS.md), the same as modules\recorder:
#   modules\<id>\module.json, icon*.svg, Directory.Build.props (its own, so the folder can move out), Rigbay.<Ns>.slnx
#   modules\<id>\src\Rigbay.<Ns>\Rigbay.<Ns>.csproj, <Ns>Module.cs, lang\en.lang, lang\vi.lang
# The module still has the sample's rows, hotkey, tray item and commands (a tour of the contract; delete what you do not need) and the neutral icon
# from assets\brand\modules. It has no tests yet: add modules\<id>\tests\Rigbay.<Ns>.Tests (see modules\recorder for a fake host).
#
# It writes only under modules\<id>. It refuses an id that exists, a reserved id and a namespace that is not a C# name. -WhatIf prints the plan.
# -Build runs `dotnet build` and the language check on the result.
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Id,         # lowercase words joined by '-', 2 to 40 characters: fan-tools
    [string]$Namespace,                         # default: the id in PascalCase (FanTools); the assembly and namespace become Rigbay.<Namespace>
    [string]$Name,                              # shown to people, 1 to 60 characters. Default: the id with spaces and a capital
    [string]$Description,                        # one sentence, up to 200 characters. Default: the sample's
    [ValidateSet('power', 'cooling', 'network', 'display', 'monitoring', 'other')][string]$Group = 'other',
    [string]$Author = 'Rigbay contributors',
    [switch]$Build,
    [string]$RepoRoot                           # default: the repository this script is in (tests use a scratch copy)
)
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Join-Path $PSScriptRoot '..' }
$root = (Resolve-Path $RepoRoot).Path
$utf8 = New-Object System.Text.UTF8Encoding($false)

# --- input ------------------------------------------------------------------------------------------------------------------
if ($Id -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$' -or $Id.Length -lt 2 -or $Id.Length -gt 40) { throw "id '$Id': lowercase letters and digits in words joined by single '-', 2 to 40 characters (docs/ABSTRACTIONS.md 2.1)" }
if (@('rigbay', 'host', 'help', 'list', 'version', 'sample') -contains $Id) { throw "id '$Id' is reserved (the host's words, or the sample's own id)" }
if (-not $Namespace) { $Namespace = (($Id -split '-') | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join '' }
if ($Namespace -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "namespace '$Namespace' is not one C# identifier (it is placed after 'Rigbay.')" }
if (-not $Name) { $Name = $Id.Substring(0, 1).ToUpperInvariant() + $Id.Substring(1).Replace('-', ' ') }
if ($Name.Length -gt 60) { throw '-Name must be 1 to 60 characters' }
if ($Description -and $Description.Length -gt 200) { throw '-Description is one sentence of at most 200 characters' }
foreach ($v in @($Name, $Description, $Author)) { if ($v -and $v -match '["\\\r\n]') { throw 'quotes, backslashes and line breaks are not allowed in -Name, -Description and -Author' } }

$sample = Join-Path $root 'samples\Rigbay.SampleModule'
$icons = Join-Path $root 'assets\brand\modules'
$dest = Join-Path $root "modules\$Id"
foreach ($p in @($sample, $icons, (Join-Path $root 'src\Rigbay.Abstractions\Rigbay.Abstractions.csproj'))) { if (-not (Test-Path $p)) { throw "$p not found: is $root the Rigbay repository?" } }
if (Test-Path $dest) { throw "modules\$Id already exists" }
$asm = "Rigbay.$Namespace"

# --- the rewrite of a text -----------------------------------------------------------------------------------------------------
# The sample's Vietnamese name (read from vi.lang, so that this script stays ASCII) is replaced by the new name as well.
$viLang = [System.IO.File]::ReadAllText((Join-Path $sample 'lang\vi.lang'), $utf8)
$viName = [regex]::Match($viLang, '(?m)^module\.sample\.name\s*=\s*(.+?)\s*$').Groups[1].Value
$mau = 'm' + [char]0x1EAB + 'u'   # the Vietnamese word for "sample"
function ConvertTo-NewText { param([string]$Text)
    $t = $Text
    # order matters: the longest names first
    $t = $t.Replace('Rigbay.SampleModule', $asm).Replace('SampleModule', "${Namespace}Module").Replace('SampleSettings', "${Namespace}Settings")
    $t = $t.Replace('module.sample.', "module.$Id.").Replace('"sample"', "`"$Id`"").Replace('sample.ticks', "$Id.ticks").Replace('rigbay sample', "rigbay $Id")
    $t = $t.Replace('Sample module', $Name).Replace('the sample timer', 'the timer')
    if ($viName) { $t = $t.Replace($viName, $Name) }
    $t = [regex]::Replace($t, "(?i)module $mau", $Name)
    # the tray text starts with the name of the module: "Sample: toggle echo", "<vi>: bat tat echo"
    $t = [regex]::Replace($t, "(?m)^(module\.$([regex]::Escape($Id))\.tray\.toggle\s*=\s*)[^:\r\n]+:", { param($m) $m.Groups[1].Value + $Name + ':' })
    return $t
}
function Read-Text { param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $skip = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    return $utf8.GetString($bytes, $skip, $bytes.Length - $skip)
}

# --- build the files in memory first, so a mistake writes nothing ---------------------------------------------------------------
$out = [ordered]@{}

$manifest = ConvertTo-NewText (Read-Text (Join-Path $sample 'module.json'))
$manifest = [regex]::Replace($manifest, '("description":\s*")[^"]*(")', { param($m) $m.Groups[1].Value + $(if ($Description) { $Description } else { 'A starting point for a Rigbay module. It shows every kind of row and changes nothing on the machine.' }) + $m.Groups[2].Value })
$manifest = [regex]::Replace($manifest, '("group":\s*")[^"]*(")', { param($m) $m.Groups[1].Value + $Group + $m.Groups[2].Value })
$manifest = [regex]::Replace($manifest, '("author":\s*")[^"]*(")', { param($m) $m.Groups[1].Value + $Author + $m.Groups[2].Value })
$manifest = [regex]::Replace($manifest, '("languages":\s*"lang")', '$1,' + "`n" + '  "icon": "icon.svg"')
$out['module.json'] = $manifest

$out["src\$asm\${Namespace}Module.cs"] =ConvertTo-NewText (Read-Text (Join-Path $sample 'SampleModule.cs'))
foreach ($lang in 'en', 'vi') {
    $text = ConvertTo-NewText (Read-Text (Join-Path $sample "lang\$lang.lang"))
    if ($Description) { $text = [regex]::Replace($text, "(?m)^(module\.$([regex]::Escape($Id))\.description\s*=\s*).*$", { param($m) $m.Groups[1].Value + $Description }) }
    $out["src\$asm\lang\$lang.lang"] = $text
}

$proj = ConvertTo-NewText (Read-Text (Join-Path $sample 'Rigbay.SampleModule.csproj'))
$proj = $proj.Replace('..\..\src\Rigbay.Abstractions\Rigbay.Abstractions.csproj', '..\..\..\..\src\Rigbay.Abstractions\Rigbay.Abstractions.csproj')
$proj = $proj.Replace('<None Include="module.json" CopyToOutputDirectory="PreserveNewest" />',
    '<None Include="..\..\module.json" CopyToOutputDirectory="PreserveNewest" LinkBase="." />' + "`r`n" +
    '    <!-- the module''s icon (manifest field "icon"): the tile, the quiet glyph and the unsupported tile sit next to module.json -->' + "`r`n" +
    '    <None Include="..\..\icon*.svg" CopyToOutputDirectory="PreserveNewest" LinkBase="." />')
if (-not $proj.Contains('..\..\..\..\src\Rigbay.Abstractions') -or -not $proj.Contains('icon*.svg')) { throw 'the sample project does not look as expected; new-module.ps1 needs updating' }
$out["src\$asm\$asm.csproj"] = $proj

$out['Directory.Build.props'] = @"
<Project>
  <!-- Self-contained on purpose: this folder can move to its own repository (tools\extract-module.ps1), so it must not
       depend on the host repository's Directory.Build.props. -->
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Deterministic>true</Deterministic>
    <Version>0.1.0</Version>
    <Authors>$Author</Authors>
    <Copyright>MIT</Copyright>
  </PropertyGroup>
</Project>
"@
$out["$asm.slnx"] = @"
<Solution>
  <Project Path="../../src/Rigbay.Abstractions/Rigbay.Abstractions.csproj" />
  <Project Path="src/$asm/$asm.csproj" />
</Solution>
"@
$iconFiles = [ordered]@{ 'icon.svg' = 'module-template.svg'; 'icon-glyph.svg' = 'module-template-glyph.svg'; 'icon-unsupported.svg' = 'module-template-unsupported.svg' }
foreach ($k in $iconFiles.Keys) {
    $src = Join-Path $icons $iconFiles[$k]
    if (-not (Test-Path $src)) { throw "$src not found" }
    $out[$k] = Read-Text $src
}

# a rename that left the sample's name behind is a bug of this script: look for it before writing
$left = @()
foreach ($k in $out.Keys) { if ($k -notlike 'icon*' -and ($out[$k] -cmatch 'SampleModule|SampleSettings|module\.sample\.|Sample module')) { $left += $k } }
if ($left.Count -gt 0) { throw "the sample's name is left in: $($left -join ', ')" }

Write-Host "== new module '$Id' ($Name), assembly and namespace $asm, group $Group -> modules\$Id"
foreach ($k in $out.Keys) { Write-Host "   write  modules\$Id\$k" }
if (-not $PSCmdlet.ShouldProcess($dest, "create the module $Id from the sample")) { Write-Host '(nothing was written)'; return }

foreach ($k in $out.Keys) {
    $path = Join-Path $dest $k
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [System.IO.File]::WriteAllText($path, $out[$k], $utf8)
}
Write-Host "== created modules\$Id"

if ($Build) {
    $sln = Join-Path $dest "$asm.slnx"
    Write-Host '== dotnet build'
    & dotnet build $sln -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
    Write-Host '== language check'
    & dotnet run --project (Join-Path $root 'src\Rigbay.LangCheck') -c Release -- (Join-Path $dest "src\$asm\lang") --module $Id --source (Join-Path $dest 'src') --strict
    if ($LASTEXITCODE -ne 0) { throw 'the language check found problems' }
}
Write-Host ''
Write-Host "Next: edit modules\$Id\src\$asm\${Namespace}Module.cs and the lang files, write the real requires in module.json, replace the icon, add tests"
Write-Host "(modules\recorder has a fake host), and when it is finished and verified: docs\MODULE-REPOS.md."
