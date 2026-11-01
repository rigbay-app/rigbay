# Turns modules\<id> of this repository into its own standalone repository (local only: this script never pushes and never
# talks to GitHub except to read: git ls-remote, gh api for the action commit SHAs).
#
#   .\tools\extract-module.ps1 -ModuleId predatorsense -OutDir D:\Documents\Projects\_repos\module-predatorsense [-Verify]
#
# What it does (docs\MODULE-REPOS.md has the reasoning and the steps after it):
#   1. git subtree split of modules/<id> on -SourceBranch (read-only for this repo: no ref, no file is changed here), so the
#      history of the module comes along. The new repo's `main` starts from that.
#   2. Standalone layout: the host contract is the git submodule external/rigbay, pinned to -HostCommit (default: tip of main on
#      -HostUrl), and every ProjectReference / solution path to ..\src\Rigbay.Abstractions|Core points into it. No package is published.
#   3. .gitignore, LICENSE (the host's), .github/workflows/build.yml (windows-latest, SDK 8.x and 10.x, read-only token, actions
#      pinned by full commit SHA, no secrets, no publishing), a README skeleton built from module.json, and docs/<id>/ of the host
#      copied into docs/ of the new repo.
#   4. With -Verify: fresh `git clone --recurse-submodules` into a temp folder, then dotnet build and dotnet test there.
# Afterwards: edit the README (it is a skeleton), scan the repo (docs\MODULE-REPOS.md), and only then create the GitHub repo and push.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModuleId,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$SourceBranch = 'main',
    [string]$HostUrl = 'https://github.com/rigbay-app/rigbay.git',
    [string]$HostCommit,                 # default: the tip of refs/heads/main on $HostUrl (a commit that exists remotely)
    [string]$RepoName,                   # default: module-<ModuleId>
    [string[]]$ExcludeDocs = @('GATE-*'),  # files of docs/<id> that are workflow notes of the host, not documentation
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$root = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if (-not $RepoName) { $RepoName = "module-$ModuleId" }
$nl = "`n"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# First argument is the repository folder, the rest goes to git as is (no param block, so -q, -m, -A are not taken as parameters).
function Invoke-Git {
    $dir = $args[0]; $rest = @($args | Select-Object -Skip 1)
    & git -C $dir @rest
    if ($LASTEXITCODE -ne 0) { throw "git $($rest -join ' ') failed ($LASTEXITCODE) in $dir" }
}
function Get-GitOut {
    $dir = $args[0]; $rest = @($args | Select-Object -Skip 1)
    $o = & git -C $dir @rest
    if ($LASTEXITCODE -ne 0) { throw "git $($rest -join ' ') failed ($LASTEXITCODE) in $dir" }
    ($o -join $nl).Trim()
}
function Write-Text { param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}
# Rewrites a text file, keeping its BOM if it had one.
function Edit-File { param([string]$Path, [scriptblock]$Edit)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($Path)
    $new = & $Edit $text
    [System.IO.File]::WriteAllText($Path, $new, (New-Object System.Text.UTF8Encoding($bom)))
}
function Action-Sha { param([string]$Repo)
    $tag = (& gh api "repos/$Repo/releases/latest" --jq .tag_name).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $tag) { throw "no release found for $Repo" }
    $ref = @(& gh api "repos/$Repo/git/ref/tags/$tag" --jq '.object.type,.object.sha')
    if ($ref[0] -eq 'tag') { $ref = @('commit', (& gh api "repos/$Repo/git/tags/$($ref[1])" --jq .object.sha).Trim()) }
    if ($ref[0] -ne 'commit' -or $ref[1] -notmatch '^[0-9a-f]{40}$') { throw "cannot resolve $Repo@$tag to a commit" }
    @{ Sha = $ref[1]; Tag = $tag }
}

# ---- 0. checks -------------------------------------------------------------------------------------------------------------
$modulePath = "modules/$ModuleId"
$manifestJson = Get-GitOut $root show "${SourceBranch}:$modulePath/module.json"
$manifest = $manifestJson | ConvertFrom-Json
if ($manifest.id -ne $ModuleId) { throw "module.json id '$($manifest.id)' differs from the folder name '$ModuleId'" }
if (Test-Path $OutDir) { if (Get-ChildItem $OutDir -Force | Select-Object -First 1) { throw "$OutDir exists and is not empty" } }
if (-not $HostCommit) {
    $line = (& git ls-remote $HostUrl refs/heads/main) -join ''
    if ($LASTEXITCODE -ne 0 -or $line -notmatch '^([0-9a-f]{40})') { throw "cannot read main of $HostUrl" }
    $HostCommit = $Matches[1]
}
"module $ModuleId from $SourceBranch; host pin $HostCommit"

# ---- 1. history ------------------------------------------------------------------------------------------------------------
$split = Get-GitOut $root subtree split -q -P $modulePath $SourceBranch
if ($split -notmatch '^[0-9a-f]{40}$') { throw "subtree split did not return a commit: $split" }
"split: $split"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
Invoke-Git $OutDir init -q -b main
# The new repository is not a worktree of this one: give it the same author identity (the commits are made by this script).
foreach ($k in 'user.name', 'user.email') { Invoke-Git $OutDir config $k (Get-GitOut $root config $k) }
# The split commit is not on any ref of this repo (we do not create one); allow fetching it by id for this one local fetch.
$env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = 'uploadpack.allowAnySHA1InWant'; $env:GIT_CONFIG_VALUE_0 = 'true'
try { Invoke-Git $OutDir fetch -q $root $split } finally { Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0 }
Invoke-Git $OutDir reset -q --hard FETCH_HEAD

# ---- 2. standalone layout: host contract as a submodule --------------------------------------------------------------------
Invoke-Git $OutDir submodule add -q $HostUrl external/rigbay
Invoke-Git $OutDir/external/rigbay checkout -q --detach $HostCommit
if (-not (Test-Path "$OutDir\external\rigbay\src\Rigbay.Abstractions\Rigbay.Abstractions.csproj")) { throw "the pinned host commit has no Rigbay.Abstractions" }
Invoke-Git $OutDir add external/rigbay .gitmodules

$comment = @"
<!--
    Standalone module repository. The host contract is the git submodule external/rigbay (pinned to a commit of
    https://github.com/rigbay-app/rigbay); nothing is published as a package. Clone with the recurse-submodules option.
    Rigbay.Abstractions.dll must NOT end up in the output folder: the host supplies its own copy.
  -->
"@
foreach ($rel in (& git -C $OutDir ls-files -- '*.csproj' '*.sln' '*.props')) {
    $depth = ($rel -split '/').Count - 1
    $replacement = (('..\' * $depth) + 'external\rigbay\') + 'src\$1'
    $isProject = $rel -like '*.csproj'
    Edit-File (Join-Path $OutDir $rel) {
        param($t)
        $t = [regex]::Replace($t, '(?:\.\.\\)+src\\(Rigbay\.(?:Abstractions|Core)\\)', $replacement)
        if ($isProject) { $t = [regex]::Replace($t, '(?s)<!--(?:(?!-->).)*?ProjectReference(?:(?!-->).)*?-->', $comment.TrimEnd()) }
        $t
    }
}
Invoke-Git $OutDir add -A
Invoke-Git $OutDir commit -q -m "Standalone layout: the host contract is the submodule external/rigbay ($($HostCommit.Substring(0,7)))" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"

# ---- 3. repository files ---------------------------------------------------------------------------------------------------
Write-Text "$OutDir\.gitignore" ("# build output`nbin/`nobj/`nTestResults/`n*.nupkg`n# editors and local files`n.vs/`n.vscode/`n*.user`n*.log`n")
Write-Text "$OutDir\LICENSE" ((Get-GitOut $root show "${SourceBranch}:LICENSE") + $nl)

$co = Action-Sha 'actions/checkout'
$sd = Action-Sha 'actions/setup-dotnet'
$sln = (Get-ChildItem $OutDir -Filter *.sln -File | Select-Object -First 1).Name
if (-not $sln) { throw 'no solution file at the root of the module' }
Write-Text "$OutDir\.github\workflows\build.yml" (@"
name: build

on:
  push:
  pull_request:

# Read-only token, no secrets, nothing is published from this workflow.
permissions:
  contents: read

jobs:
  build:
    runs-on: windows-latest
    steps:
      - name: Check out (with the host contract submodule)
        uses: actions/checkout@$($co.Sha) # $($co.Tag)
        with:
          submodules: true
          persist-credentials: false

      - name: Set up .NET
        uses: actions/setup-dotnet@$($sd.Sha) # $($sd.Tag)
        with:
          dotnet-version: |
            8.x
            10.x

      - name: Build
        run: dotnet build $sln --configuration Release --nologo

      - name: Test
        run: dotnet test $sln --configuration Release --no-build --nologo
"@ + $nl)
Invoke-Git $OutDir add -A
Invoke-Git $OutDir commit -q -m "CI: build and test on windows-latest (read-only token, actions pinned by SHA)" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"

# ---- 4. README skeleton and docs -------------------------------------------------------------------------------------------
$bullets = { param($items) if ($items -and @($items).Count) { (@($items) | ForEach-Object { "- ``$_``" }) -join $nl } else { '- nothing' } }
$req = $manifest.requires
Write-Text "$OutDir\README.md" @"
# $($manifest.name) module for Rigbay

$($manifest.description)

This is a module for [Rigbay](https://github.com/rigbay-app/rigbay), a Windows app that manages modules for your own machine.
Version $($manifest.version). Early development: not released, no package published.

> This README is a skeleton generated by ``tools/extract-module.ps1``: replace the parts marked TODO before publishing.

## Supported machines

TODO: say honestly what was tested and on what.

## Build

Needs Windows, the .NET 8 SDK or newer (the module targets ``net8.0``). The host contract (``Rigbay.Abstractions``) comes from the
git submodule ``external/rigbay``, so clone with submodules:

``````powershell
git clone --recurse-submodules https://github.com/rigbay-app/$RepoName.git
cd $RepoName
dotnet build $sln --configuration Release
dotnet test $sln --configuration Release
``````

The module folder is the build output of the module project (``src\<project>\bin\Release\net8.0``).

## Install into Rigbay

Rigbay reads modules from ``%ProgramData%\Rigbay\modules\<id>\`` (the folder name must equal the manifest id). Copy the whole build output
folder there, as ``%ProgramData%\Rigbay\modules\$ModuleId\``, with ``module.json`` at the top of that folder, then start Rigbay (as
administrator) and open the module page; the first time, the app lists what the module declares it uses and asks you to accept it.
``Rigbay.Abstractions.dll`` must not be in the folder (the host ignores it and uses its own).

## What the module declares it uses

Administrator: $($req.admin)

Network:
$(& $bullets $req.network)

Registry:
$(& $bullets $req.registry)

Services:
$(& $bullets $req.services)

## Safety

TODO: what the module can change on the machine, what is allowed, what is not verified.

## License

MIT, see [LICENSE](LICENSE).
"@
$docsDir = "$env:TEMP\extract-module-docs-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $docsDir | Out-Null
try {
    $hasDocs = (& git -C $root ls-tree -r --name-only $SourceBranch "docs/$ModuleId") -ne $null
    if ($hasDocs) {
        $zip = "$docsDir\docs.zip"
        & git -C $root archive --format=zip -o $zip $SourceBranch "docs/$ModuleId"
        if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
        Expand-Archive -LiteralPath $zip -DestinationPath $docsDir
        New-Item -ItemType Directory -Force "$OutDir\docs" | Out-Null
        Get-ChildItem "$docsDir\docs\$ModuleId" -Recurse -File | ForEach-Object {
            $name = $_.Name
            if ($ExcludeDocs | Where-Object { $name -like $_ }) { return }
            $dest = Join-Path "$OutDir\docs" $_.FullName.Substring("$docsDir\docs\$ModuleId\".Length)
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            if ($_.Extension -in '.md', '.txt', '.log') {
                # Logs name the developer's worktree (an absolute path on the author's machine): replace it before it reaches the history.
                $text = [System.IO.File]::ReadAllText($_.FullName)
                $text = [regex]::Replace($text, '[A-Za-z]:\\[^\r\n"''<>|*?]*?\\\.claude\\worktrees\\[^\\\s]+\\', '<worktree>\')
                [System.IO.File]::WriteAllText($dest, $text, $utf8)
            } else { Copy-Item $_.FullName $dest }
        }
    }
} finally { Remove-Item $docsDir -Recurse -Force -ErrorAction SilentlyContinue }
# Last check before the commit: nothing of the developer's machine in the new files (the pushed history keeps them forever).
$bad = @()
foreach ($needle in @($env:USERNAME, $env:COMPUTERNAME, '.claude\worktrees', 'C:\Users\')) {
    if ($needle) { $bad += @(& git -C $OutDir grep -n -I -i -F --untracked -e $needle -- . ':!external' ':!LICENSE' | Select-Object -First 5) }
}
if ($bad.Count) { throw "personal data found, fix before committing:`n$($bad -join "`n")" }
Invoke-Git $OutDir add -A
Invoke-Git $OutDir commit -q -m "README skeleton, LICENSE, .gitignore, documentation of the module copied from the host repository" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"

# ---- 5. optional: prove a fresh clone builds and tests ---------------------------------------------------------------------
if ($Verify) {
    $fresh = Join-Path $env:TEMP "extract-module-verify-$([guid]::NewGuid().ToString('N'))"
    "fresh clone -> $fresh"
    & git clone -q --recurse-submodules $OutDir $fresh
    if ($LASTEXITCODE -ne 0) { throw 'fresh clone failed' }
    Push-Location $fresh
    try {
        & dotnet build $sln --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed in the fresh clone' }
        & dotnet test $sln --configuration Release --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed in the fresh clone' }
    } finally { Pop-Location }
    "verified in $fresh (delete it when done)"
}
"ready: $OutDir (no remote, nothing pushed)"
