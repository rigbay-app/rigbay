# Builds the release zip of a module from a CLEAN, FRESH clone of its repository, exactly as the installer expects it
# (docs\INSTALL.md section 5), and prints its size and SHA-256. Local only: it never pushes, never creates a release and never
# changes the repository it is given (gh is used only to read the CI result of the commit).
#
#   .\tools\package-module.ps1 -Repo D:\Documents\Projects\_repos\module-xtu
#   .\tools\package-module.ps1 -Repo https://github.com/rigbay-app/module-xtu.git -OutDir D:\Documents\Projects\_repos\_dist -CheckReproducible
#
# What it does (docs\RELEASING.md has the procedure around it):
#   1. `git clone --recurse-submodules --branch main` of the repository's origin (or of the URL) into a short scratch folder.
#      A local -Repo must be clean and its HEAD must equal the cloned main HEAD: the zip is made from what is on GitHub.
#   2. Refuses unless a completed, successful GitHub Actions run exists for exactly that commit (-SkipCiCheck to override).
#   3. Reads module.json (the shallowest one), finds the project whose assembly is the manifest's entry assembly, and runs
#      `dotnet publish -c Release` with a path map, so the build does not contain the path of the scratch folder.
#   4. Assembles the zip: module.json first, then everything else in ordinal order; module DLLs and the dependencies that the
#      publish output has (deps.json, runtimes\), lang\, icons, other files the project copies (for example steam-domains.txt),
#      LICENSE and a README.txt stub. NOT: Rigbay.Abstractions, Rigbay.Core, Windows App SDK / WinRT files, PDBs, XML documentation,
#      executables. Timestamps are the commit time, so the same commit gives the same zip.
#   5. Checks the zip: the manifest's entry assembly, icon, languages folder are in it; no forbidden file; no path of this
#      machine (the scratch folder, the user profile, the user name, the machine name) in any file.
#   6. -CheckReproducible builds a second time in another folder and requires the same SHA-256.
#   7. Unless -NoVerify, runs tools\verify-module-zip (the host's own Core.Install classes: SHA-256, unzip, manifest checks, host API
#      range, declares, then the real loader in a scratch folder). A zip that does not pass is deleted and the script fails.
#
# Output: <OutDir>\<id>-<version>.zip, <id>-<version>.zip.sha256 and <id>-<version>.build.json (commit, tool versions, file list).
# Same commit + same SDK + same PowerShell edition = same zip. A different PowerShell edition or .NET runtime may compress the same
# bytes differently, so the hash that counts is the one of the file that was uploaded to the release (and is in the registry).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repo,          # a clone of the module repository (its origin is used) or an https URL
    [string]$OutDir,                              # default: tools\out\dist
    [string]$Branch = 'main',
    [string]$WorkDir,                             # scratch parent; default: <temp>\rb-pkg (keep it short)
    [switch]$SkipCiCheck,
    [switch]$CheckReproducible,
    [switch]$NoVerify,
    [switch]$KeepWork,
    [switch]$AllowLocalDifference                 # -Repo is a folder whose HEAD or working tree is not what origin has
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$hostRoot = (& git -C $here rev-parse --show-toplevel).Trim()
if (-not $OutDir) { $OutDir = Join-Path $here 'out\dist' }
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) 'rb-pkg' }
$utf8 = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# --- helpers --------------------------------------------------------------------------------------------------------------
function Invoke-Native { param([string]$Exe, [string[]]$Arguments, [switch]$Quiet)
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $lines = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
    if (-not $Quiet) { $lines | Write-Host }
    if ($code -ne 0) { if ($Quiet) { $lines | Write-Host }; throw "$Exe $($Arguments -join ' ') failed ($code)" }
    return $lines
}
function Get-GitOut { param([string]$Dir, [string[]]$GitArgs)
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & git -C $Dir @GitArgs 2>$null; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
    if ($code -ne 0) { throw "git $($GitArgs -join ' ') failed ($code) in $Dir" }
    return (($o -join "`n").Trim())
}
function Remove-Tree { param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
    catch { & cmd.exe /c "rd /s /q `"$Path`"" | Out-Null }
}
function Get-Sha256 { param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $s = [System.IO.File]::OpenRead($Path)
    try { ([System.BitConverter]::ToString($sha.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $s.Dispose(); $sha.Dispose() }
}
# bytes -> text 1:1 (Latin-1), so a plain ordinal search finds ASCII and, with an encoded needle, UTF-16 strings in a binary
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
function Test-Contains { param([byte[]]$Bytes, [string]$Needle)
    if ([string]::IsNullOrEmpty($Needle)) { return $false }
    $hay = $latin1.GetString($Bytes)
    foreach ($n in @($Needle, $Needle.Replace('\', '/'), $Needle.Replace('/', '\'))) {
        if ($hay.IndexOf($latin1.GetString([System.Text.Encoding]::UTF8.GetBytes($n)), [System.StringComparison]::Ordinal) -ge 0) { return $true }
        if ($hay.IndexOf($latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($n)), [System.StringComparison]::Ordinal) -ge 0) { return $true }
    }
    return $false
}

# --- what is built ---------------------------------------------------------------------------------------------------------
# Files that must never be in a module zip: the host supplies them, or they are not part of a release.
$forbidden = @(
    '^Rigbay\.Abstractions\.',            # the host's own copy is used (type identity of IModule)
    '^Rigbay\.Core\.',
    '^Microsoft\.WindowsAppSDK',
    '^Microsoft\.Windows\.SDK',
    '^Microsoft\.WinUI',
    '^Microsoft\.Windows\.Widgets',
    '^Microsoft\.Web\.WebView2',
    '^WinRT\.Runtime',
    '\.pdb$',                             # embedded in the dll instead (path-mapped)
    '\.xml$',                             # XML documentation of libraries
    '\.exe$', '\.pdb$', '\.zip$', '\.nupkg$',
    '\.runtimeconfig\.json$'
)

function New-Package {
    param([string]$Work, [string]$Tag)   # returns an object with the zip path and facts
    $src = Join-Path $Work 'src'
    Remove-Tree $Work
    New-Item -ItemType Directory -Force $Work | Out-Null

    Write-Host "== clone $cloneUrl ($Branch) into $src"
    Invoke-Native git @('clone', '--quiet', '--recurse-submodules', '--branch', $Branch, $cloneUrl, $src) | Out-Null
    $head = Get-GitOut $src @('rev-parse', 'HEAD')
    $shortHead = $head.Substring(0, 7)
    $commitTime = [DateTimeOffset]::Parse((Get-GitOut $src @('show', '-s', '--format=%cI', 'HEAD'))).ToUniversalTime()
    if ((Get-GitOut $src @('status', '--porcelain')) -ne '') { throw 'the fresh clone is not clean' }
    $modStatus = Get-GitOut $src @('submodule', 'status', '--recursive')
    if ($modStatus -match '^[+\-U]') { throw "a submodule of the fresh clone is not at its pinned commit:`n$modStatus" }
    Write-Host "   HEAD $head ($($commitTime.ToString('s'))Z)"
    if ($localHead -and $localHead -ne $head -and -not $AllowLocalDifference) {
        throw "the local clone is at $localHead but $Branch of $cloneUrl is ${head}: push or pull first, so the zip is what GitHub has (-AllowLocalDifference to build anyway)"
    }

    # --- CI must have passed on exactly this commit
    if (-not $SkipCiCheck -and $Tag -eq 'first') {
        if ($cloneUrl -notmatch 'github\.com[/:]([^/]+)/([^/]+?)(\.git)?$') { throw "cannot tell the GitHub repository from $cloneUrl (use -SkipCiCheck)" }
        $slug = "$($Matches[1])/$($Matches[2])"
        $runs = Invoke-Native gh @('run', 'list', '--repo', $slug, '--commit', $head, '--json', 'status,conclusion,workflowName,url', '--jq', '.[] | [.workflowName,.status,.conclusion,.url] | @tsv') -Quiet
        $runs | ForEach-Object { Write-Host "   CI: $_" }
        if (-not ($runs | Where-Object { $_ -match "`tcompleted`tsuccess`t" })) { throw "no successful CI run for commit $head of $slug" }
    }

    # --- the manifest and the project
    $mj = (Get-GitOut $src @('ls-files', '--', '*module.json')) -split "`n" | Where-Object { $_ -and $_ -notlike 'external/*' } |
        Sort-Object { ($_ -split '/').Count }, { $_ } | Select-Object -First 1
    if (-not $mj) { throw 'no module.json in the repository' }
    $manifestPath = Join-Path $src $mj
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $m = $utf8.GetString($manifestBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    $entryDll = [string]$m.entry.assembly
    $entryName = [System.IO.Path]::GetFileNameWithoutExtension($entryDll)
    $proj = Get-ChildItem -Path $src -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '[\\/](external|tests)[\\/]' -and $_.Name -notmatch '\.Tests\.csproj$' } | Where-Object {
        $x = [xml](Get-Content -Raw -LiteralPath $_.FullName)
        $an = @($x.Project.PropertyGroup.AssemblyName) | Where-Object { $_ } | Select-Object -First 1
        if (-not $an) { $an = [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }
        $an -eq $entryName
    } | Select-Object -First 1
    if (-not $proj) { throw "no project builds the entry assembly $entryDll" }
    $id = [string]$m.id; $version = [string]$m.version
    Write-Host "== $id $version, project $($proj.FullName.Substring($src.Length + 1))"

    # --- publish (path map: the scratch folder does not end up in the dlls)
    $pub = Join-Path $Work 'pub'
    $map = ($src.TrimEnd('\') + '\') + '=/_/'
    Invoke-Native dotnet @('publish', $proj.FullName, '-c', 'Release', '-o', $pub, '--nologo', '-v', 'q',
        '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true', '-p:DebugType=embedded', '-p:DebugSymbols=true',
        "-p:PathMap=$map", '-p:SatelliteResourceLanguages=en', '-p:UseAppHost=false') | Out-Null
    if (-not (Test-Path (Join-Path $pub $entryDll))) { throw "the publish output has no $entryDll" }

    # --- collect: relative path (with /) -> source file
    $files = @{}
    foreach ($f in Get-ChildItem -Path $pub -Recurse -File) {
        $rel = $f.FullName.Substring($pub.Length + 1).Replace('\', '/')
        $leaf = $f.Name
        $skip = $false
        foreach ($rx in $forbidden) { if ($leaf -match $rx) { $skip = $true; break } }
        if (-not $skip) { $files[$rel] = $f.FullName }
    }
    # module.json must be the one of the repository, byte for byte
    if (-not $files.ContainsKey('module.json')) { throw 'the publish output has no module.json (the project does not copy it)' }
    $a = [System.IO.File]::ReadAllBytes($files['module.json'])
    if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$manifestBytes)) { throw 'module.json in the publish output differs from the repository file' }
    $licence = Join-Path $src 'LICENSE'
    if (-not (Test-Path $licence)) { throw 'the repository has no LICENSE' }
    $files['LICENSE'] = $licence
    $repoUrl = ($cloneUrl -replace '\.git$', '')
    $readme = @(
        "$($m.name) $version", '',
        [string]$m.description, '',
        "A Rigbay module (id: $id). Install it from the Modules page of Rigbay; Rigbay checks this file against the list in",
        'https://github.com/rigbay-app/registry before it unpacks anything.',
        "Host API: $($m.hostApi)",
        "Source: $repoUrl (commit $head)",
        'Licence: MIT, see LICENSE.', ''
    ) -join "`n"
    $readmePath = Join-Path $Work 'README.txt'
    [System.IO.File]::WriteAllText($readmePath, $readme, $utf8)
    $files['README.txt'] = $readmePath

    # --- checks on the file set
    $problems = New-Object System.Collections.Generic.List[string]
    if (-not $files.ContainsKey($entryDll)) { $problems.Add("entry assembly $entryDll is not in the zip") }
    $iconName = if ($m.PSObject.Properties['icon']) { [string]$m.icon } else { 'icon.svg' }
    if ($iconName -and -not $files.ContainsKey($iconName.Replace('\', '/'))) { $problems.Add("icon $iconName is not in the zip") }
    $langDir = if ($m.PSObject.Properties['languages']) { [string]$m.languages } else { 'lang' }
    if (-not ($files.Keys | Where-Object { $_ -like "$langDir/*.lang" })) { $problems.Add("no $langDir/*.lang in the zip") }
    foreach ($k in $files.Keys) {
        if ($k -match '(^|/)(\.\.?)(/|$)' -or $k -match ':' -or $k.StartsWith('/')) { $problems.Add("bad path $k") }
        if ($k.Length -gt 200) { $problems.Add("path longer than 200: $k") }
        if ($k -match '(?i)Rigbay\.Abstractions') { $problems.Add("forbidden file $k") }
    }
    # no path of this machine in any file (text and binary, ASCII and UTF-16)
    $needles = @($src, $src.TrimEnd('\'), $Work, $env:USERPROFILE, [System.IO.Path]::GetTempPath().TrimEnd('\'), $env:USERNAME, $env:COMPUTERNAME) |
        Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
    foreach ($k in ($files.Keys | Sort-Object)) {
        $bytes = [System.IO.File]::ReadAllBytes($files[$k])
        foreach ($n in $needles) { if (Test-Contains $bytes $n) { $problems.Add("$k contains a path or name of this machine ('$n')") } }
        if ($k -match '\.(json|txt|lang|svg|md)$') {
            $text = $utf8.GetString($bytes)
            if ($text -match '(?<![A-Za-z0-9])[A-Za-z]:\\(?!\\)[^\s"]*\\') { $problems.Add("$k contains an absolute Windows path") }
        }
    }
    if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host "   PROBLEM: $_" -ForegroundColor Red }; throw "the file set of $id is not acceptable" }

    # --- the zip: module.json first, then ordinal order, commit time as timestamp
    $stamp = $commitTime
    if ($stamp.Year -lt 1980) { $stamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero) }
    $stamp = [DateTimeOffset]::new($stamp.Year, $stamp.Month, $stamp.Day, $stamp.Hour, $stamp.Minute, $stamp.Second - ($stamp.Second % 2), [TimeSpan]::Zero)
    $sorted = New-Object 'System.Collections.Generic.List[string]'
    foreach ($k in $files.Keys) { if ($k -ne 'module.json') { $sorted.Add([string]$k) } }
    $sorted.Sort([System.StringComparer]::Ordinal)   # ordinal, not the culture's order; the hashtable's own order is not stable
    $names = @('module.json') + $sorted.ToArray()
    $zipPath = Join-Path $Work "$id-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($n in $names) {
            $e = $zip.CreateEntry($n, [System.IO.Compression.CompressionLevel]::Optimal)
            $e.LastWriteTime = $stamp
            $in = [System.IO.File]::OpenRead($files[$n])
            $out = $e.Open()
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        }
    } finally { $zip.Dispose() }

    return [pscustomobject]@{
        Id = $id; Version = $version; Name = [string]$m.name; HostApi = [string]$m.hostApi
        Head = $head; CommitTime = $commitTime; Zip = $zipPath; Sha256 = (Get-Sha256 $zipPath); Size = (Get-Item $zipPath).Length
        Files = $names; Manifest = $manifestPath; Src = $src; RepoUrl = $repoUrl; Entry = $entryDll
    }
}

# --- main -----------------------------------------------------------------------------------------------------------------
$localHead = $null
if (Test-Path -LiteralPath $Repo) {
    $Repo = (Resolve-Path -LiteralPath $Repo).Path
    $cloneUrl = Get-GitOut $Repo @('remote', 'get-url', 'origin')
    $localHead = Get-GitOut $Repo @('rev-parse', 'HEAD')
    if (-not $AllowLocalDifference) {
        if ((Get-GitOut $Repo @('status', '--porcelain')) -ne '') { throw "$Repo has uncommitted changes (the zip is built from a fresh clone of origin, not from this folder; -AllowLocalDifference to ignore)" }
        $cur = Get-GitOut $Repo @('rev-parse', '--abbrev-ref', 'HEAD')
        if ($cur -ne $Branch) { throw "$Repo is on '$cur', not '$Branch'" }
    }
} else { $cloneUrl = $Repo }
if ($cloneUrl -notmatch '^https://') { throw "origin is '$cloneUrl': the source must be an https GitHub URL" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$r = New-Package -Work (Join-Path $WorkDir 'a') -Tag 'first'
$reproduced = $null
if ($CheckReproducible) {
    Write-Host '== second build in another folder, to compare'
    $r2 = New-Package -Work (Join-Path $WorkDir 'b') -Tag 'second'
    $reproduced = ($r2.Sha256 -eq $r.Sha256)
    Write-Host ("   first  {0}`n   second {1}`n   {2}" -f $r.Sha256, $r2.Sha256, $(if ($reproduced) { 'SAME' } else { 'DIFFERENT' }))
    if (-not $reproduced) { throw 'two builds of the same commit in different folders gave different zips' }
}

$zipOut = Join-Path $OutDir "$($r.Id)-$($r.Version).zip"
Copy-Item -LiteralPath $r.Zip -Destination $zipOut -Force
[System.IO.File]::WriteAllText("$zipOut.sha256", "$($r.Sha256) *$([System.IO.Path]::GetFileName($zipOut))`n", $utf8)
$info = [ordered]@{
    id = $r.Id; version = $r.Version; hostApi = $r.HostApi; commit = $r.Head; repo = $r.RepoUrl
    zip = [System.IO.Path]::GetFileName($zipOut); sha256 = $r.Sha256; size = $r.Size
    reproducedInSecondFolder = $reproduced
    tools = [ordered]@{ dotnetSdk = (Invoke-Native dotnet @('--version') -Quiet | Select-Object -First 1); powershell = "$($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)" }
    files = $r.Files
}
[System.IO.File]::WriteAllText((Join-Path $OutDir "$($r.Id)-$($r.Version).build.json"), (($info | ConvertTo-Json -Depth 5) + "`n"), $utf8)

Write-Host ''
Write-Host "== $($r.Id) $($r.Version): $($r.Files.Count) files"
$r.Files | ForEach-Object { Write-Host "   $_" }

if (-not $NoVerify) {
    Write-Host '== verify with the host installer classes (tools\verify-module-zip)'
    $verifyProj = Join-Path $here 'verify-module-zip\verify-module-zip.csproj'
    $verifyDll = Join-Path $here 'verify-module-zip\bin\Release\net10.0\verify-module-zip.dll'
    try {
        Invoke-Native dotnet @('build', $verifyProj, '-c', 'Release', '--nologo', '-v', 'q') -Quiet | Out-Null
        if (-not (Test-Path $verifyDll)) { throw "the build of $verifyProj has no output at $verifyDll" }
        Invoke-Native dotnet @($verifyDll, 'zip', $zipOut, '--repo', $r.RepoUrl) | Out-Null
    } catch {
        Remove-Item -LiteralPath $zipOut, "$zipOut.sha256" -Force -ErrorAction SilentlyContinue
        throw "the zip did not pass the installer's own checks and was deleted: $_"
    }
}

if (-not $KeepWork) { Remove-Tree (Join-Path $WorkDir 'a'); if ($CheckReproducible) { Remove-Tree (Join-Path $WorkDir 'b') } }

Write-Host ''
Write-Host "RESULT id=$($r.Id) version=$($r.Version) commit=$($r.Head)"
Write-Host "RESULT zip=$zipOut"
Write-Host "RESULT size=$($r.Size) sha256=$($r.Sha256)"
