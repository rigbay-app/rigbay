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
#
# Sync mode (-Sync): brings what changed in modules/<id> on -SourceBranch since the extraction into the EXISTING module repository
# -OutDir (a clone, clean working tree), as a normal merge commit on top of its history (no force, no rewrite), then moves the
# submodule pin and, with -Verify, builds and tests a fresh clone. Still local only: it never pushes.
#
#   .\tools\extract-module.ps1 -ModuleId xtu -OutDir D:\Documents\Projects\_repos\module-xtu -Sync -Verify
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModuleId,   # the id in module.json (steam-bypass)
    [string]$Folder,                     # modules/<Folder> and docs/<Folder> of this repository, when it differs from the id (steambypass)
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$Sync,                       # update an existing module repository instead of creating a new one
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
if (-not $Folder) { $Folder = $ModuleId }
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

# Points every ProjectReference / solution path that went to ..\src\Rigbay.Abstractions|Core at external/rigbay (either slash, any depth),
# and replaces the comment that talks about the old reference. Idempotent: already rewritten paths do not match.
function Update-HostPaths { param([string]$Repo)
    $comment = @"
<!--
    Standalone module repository. The host contract is the git submodule external/rigbay (pinned to a commit of
    https://github.com/rigbay-app/rigbay); nothing is published as a package. Clone with the recurse-submodules option.
    Rigbay.Abstractions.dll must NOT end up in the output folder: the host supplies its own copy.
  -->
"@
    foreach ($rel in (& git -C $Repo ls-files -- '*.csproj' '*.sln' '*.slnx' '*.props')) {
        $depth = ($rel -split '/').Count - 1
        $isProject = $rel -like '*.csproj'
        Edit-File (Join-Path $Repo $rel) {
            param($t)
            # Either slash ('..\' in .csproj/.sln, '../' in .slnx); the new path keeps the slash the old one used.
            $t = [regex]::Replace($t, '(?:\.\.[\\/])+src([\\/])(Rigbay\.(?:Abstractions|Core)[\\/])', {
                param($m) $s = $m.Groups[1].Value; (('..' + $s) * $depth) + 'external' + $s + 'rigbay' + $s + 'src' + $s + $m.Groups[2].Value
            })
            # Only a comment that still talks about the old reference (the rewritten one is skipped, so a second run changes nothing).
            if ($isProject -and $t -notmatch 'Standalone module repository') {
                $t = [regex]::Replace($t, '(?s)<!--(?:(?!-->).)*?ProjectReference(?:(?!-->).)*?-->', $comment.TrimEnd())
            }
            $t
        }
    }
}

# Nothing of the developer's machine in the files (the pushed history keeps them forever). Throws with the first hits.
# $Paths limits the check to some files (sync mode: the files the merge brought in).
function Assert-NoPersonalData { param([string]$Repo, [string[]]$Paths)
    $spec = if ($Paths -and $Paths.Count) { @('--') + $Paths } else { @('--', '.', ':!external', ':!LICENSE') }
    $bad = @()
    # The Windows user and the worktree markers match in any case; the machine name only as written (the GitHub login is the same word in lower case).
    foreach ($n in @(@($env:USERNAME, 'i'), @('.claude\worktrees', 'i'), @('C:\Users\', 'i'), @($env:COMPUTERNAME, 's'))) {
        if (-not $n[0]) { continue }
        [string[]]$gitArgs = @('-C', $Repo, 'grep', '-n', '-I', '-F')
        if ($n[1] -eq 'i') { $gitArgs += '-i' }
        $gitArgs += @('--untracked', '-e', $n[0]) + $spec
        # "C:\Users\USER\" is the placeholder the host's logs use for the home folder: not personal.
        $bad += @(& git @gitArgs | Where-Object { $_.Replace('C:\Users\USER\', '').IndexOf($n[0], [StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -First 5)
    }
    if ($bad.Count) { throw "personal data found, fix before committing:`n$($bad -join "`n")" }
}

# Fresh `git clone --recurse-submodules` of $Repo into a temp folder, then dotnet build and dotnet test there.
function Invoke-FreshVerify { param([string]$Repo, [string]$Solution)
    $fresh = Join-Path $env:TEMP "extract-module-verify-$([guid]::NewGuid().ToString('N'))"
    "fresh clone -> $fresh"
    & git clone -q --recurse-submodules $Repo $fresh
    if ($LASTEXITCODE -ne 0) { throw 'fresh clone failed' }
    Push-Location $fresh
    try {
        & dotnet build $Solution --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed in the fresh clone' }
        & dotnet test $Solution --configuration Release --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed in the fresh clone' }
    } finally { Pop-Location }
    "verified in $fresh (delete it when done)"
}

# The split commit is not on any ref of the host repository (we do not create one): allow fetching it by id for this one local fetch.
function Get-SplitCommit { param([string]$Repo, [string]$Split)
    $env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = 'uploadpack.allowAnySHA1InWant'; $env:GIT_CONFIG_VALUE_0 = 'true'
    try { Invoke-Git $Repo fetch -q $root $Split } finally { Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0 }
}

# ---- 0. checks -------------------------------------------------------------------------------------------------------------
$modulePath = "modules/$Folder"
# module.json: the shallowest one in the module folder (bin/obj are not tracked); it may sit next to the project instead of at the root.
$files = @(& git -C $root ls-tree -r --name-only $SourceBranch -- $modulePath)
if ($LASTEXITCODE -ne 0 -or -not $files.Count) { throw "$modulePath does not exist on $SourceBranch" }
$manifestPath = $files | Where-Object { (Split-Path $_ -Leaf) -eq 'module.json' } | Sort-Object { ($_ -split '/').Count }, { $_ } | Select-Object -First 1
if (-not $manifestPath) { throw "no module.json under $modulePath on $SourceBranch" }
$manifest = (Get-GitOut $root show "${SourceBranch}:$manifestPath") | ConvertFrom-Json
# The solution at the module root: .slnx or .sln (-Filter *.sln would also match .slnx on Windows, so the extension is compared exactly).
$slnFiles = @($files | Where-Object { ($_ -split '/').Count -eq 3 -and $_ -match '\.(sln|slnx)$' })
if ($slnFiles.Count -ne 1) { throw "expected exactly one .sln or .slnx at the root of $modulePath, found $($slnFiles.Count)" }
$sln = Split-Path $slnFiles[0] -Leaf
# Target framework and build output folder of the module project (the one that is not a test project), for the README.
$moduleProject = $files | Where-Object { $_ -like '*.csproj' -and $_ -notmatch '[Tt]ests' } | Sort-Object { ($_ -split '/').Count } | Select-Object -First 1
$tfm = 'net8.0'; $projectDir = ''
if ($moduleProject) {
    $m = [regex]::Match((Get-GitOut $root show "${SourceBranch}:$moduleProject"), '<TargetFramework>([^<]+)</TargetFramework>')
    if ($m.Success) { $tfm = $m.Groups[1].Value }
    $projectDir = (Split-Path $moduleProject -Parent).Replace('\', '/').Substring($modulePath.Length).TrimStart('/').Replace('/', '\')
}
$buildOut = if ($projectDir) { "$projectDir\bin\Release\$tfm" } else { "bin\Release\$tfm" }
if ($manifest.id -ne $ModuleId) { throw "module.json id '$($manifest.id)' differs from -ModuleId '$ModuleId' (folder '$Folder')" }
if (-not $Sync -and (Test-Path $OutDir)) { if (Get-ChildItem $OutDir -Force | Select-Object -First 1) { throw "$OutDir exists and is not empty" } }
if (-not $HostCommit) {
    $line = (& git ls-remote $HostUrl refs/heads/main) -join ''
    if ($LASTEXITCODE -ne 0 -or $line -notmatch '^([0-9a-f]{40})') { throw "cannot read main of $HostUrl" }
    $HostCommit = $Matches[1]
}
"module $ModuleId from $SourceBranch; host pin $HostCommit"

# ---- sync mode: update an existing module repository ----------------------------------------------------------------------------
if ($Sync) {
    if (-not (Test-Path "$OutDir\.git")) { throw "$OutDir is not a git repository (-Sync needs an existing module repository)" }
    $OutDir = (Resolve-Path $OutDir).Path
    if (Get-GitOut $OutDir status --porcelain) { throw "$OutDir has uncommitted changes; commit or discard them first" }
    if (-not (Test-Path "$OutDir\external\rigbay\.git")) { Invoke-Git $OutDir submodule update --init -q }
    foreach ($k in 'user.name', 'user.email') {
        if (-not (& git -C $OutDir config $k)) { Invoke-Git $OutDir config $k (Get-GitOut $root config $k) }
    }
    $before = Get-GitOut $OutDir rev-parse HEAD
    $split = Get-GitOut $root subtree split -q -P $modulePath $SourceBranch
    if ($split -notmatch '^[0-9a-f]{40}$') { throw "subtree split did not return a commit: $split" }
    Get-SplitCommit $OutDir $split
    # The split is deterministic, so the commits extracted earlier are ancestors of this one: the merge base is the last host state the repository has.
    $base = (& git -C $OutDir merge-base $split HEAD)
    if ($LASTEXITCODE -ne 0 -or -not $base) { throw "the history of $OutDir shares nothing with the split of $modulePath (was it made by this script?)" }
    $newCommits = @(& git -C $OutDir log --format='%h %s' "$base..$split")
    "sync: split $split, merge base $base, $($newCommits.Count) new commit(s) of $modulePath"
    $newCommits | ForEach-Object { "  $_" }
    if ($newCommits.Count) {
        $changed = @(& git -C $OutDir diff --name-only $base $split)
        # A normal merge commit: the module's own commits come along with their authors, nothing is rewritten, nothing needs a force-push.
        & git -C $OutDir merge --no-ff --no-edit -q -m "Sync with Rigbay main: $($newCommits.Count) change(s) of modules/$Folder" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>" $split
        if ($LASTEXITCODE -ne 0) {
            $conflicts = @(& git -C $OutDir diff --name-only --diff-filter=U)
            & git -C $OutDir merge --abort
            throw "merge of the split conflicts in: $($conflicts -join ', '). Aborted, the repository is unchanged. Resolve by hand (docs/MODULE-REPOS.md)."
        }
        # A file that came in may still point at the old relative path of the host (new project, new reference): rewrite and commit if so.
        Update-HostPaths $OutDir
        if (Get-GitOut $OutDir status --porcelain) {
            Invoke-Git $OutDir add -A
            Invoke-Git $OutDir commit -q -m "Layout: point new references at external/rigbay" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
        }
        Assert-NoPersonalData $OutDir @($changed | Where-Object { Test-Path (Join-Path $OutDir $_) })
    }
    # The pin: a commit that exists on GitHub. Default: the tip of main there; an explicit -HostCommit must be reachable from origin/main.
    $ext = Join-Path $OutDir 'external\rigbay'
    Invoke-Git $ext fetch -q origin
    $null = & git -C $ext rev-parse -q --verify "$HostCommit^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "pin $HostCommit is not a commit of $HostUrl" }
    if (-not (Get-GitOut $ext branch -r --contains $HostCommit | Select-String 'origin/main')) { throw "pin $HostCommit is not on origin/main of $HostUrl" }
    $oldPin = Get-GitOut $OutDir rev-parse 'HEAD:external/rigbay'
    if ($oldPin -ne $HostCommit) {
        Invoke-Git $ext checkout -q --detach $HostCommit
        Invoke-Git $OutDir add external/rigbay
        Invoke-Git $OutDir commit -q -m "Host contract: move the submodule pin from $($oldPin.Substring(0,7)) to $($HostCommit.Substring(0,7)) (Rigbay main)" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
        "pin: $($oldPin.Substring(0,7)) -> $($HostCommit.Substring(0,7))"
    } else { "pin: unchanged ($($HostCommit.Substring(0,7)))" }
    # docs/<id> of the host are NOT merged: the copies in docs/ carry hand-made path fixes. List what changed on the host so it can be compared by hand.
    $baseLine = Get-GitOut $OutDir log -1 --format='%at %s' $base
    $hostBase = & git -C $root log --format='%H|%at %s' $SourceBranch -- $modulePath | Where-Object { $_.Substring(41) -eq $baseLine } | ForEach-Object { $_.Substring(0, 40) } | Select-Object -First 1
    if ($hostBase) {
        $docChanges = @(& git -C $root diff --name-status $hostBase $SourceBranch -- "docs/$Folder")
        if ($docChanges.Count) { "docs: host docs/$Folder changed since $($hostBase.Substring(0,7)); compare by hand with docs/ here (not merged):"; $docChanges | ForEach-Object { "  $_" } }
        else { "docs: host docs/$Folder unchanged since $($hostBase.Substring(0,7))" }
    } else { "docs: could not find the host commit of the merge base; compare docs/$Folder by hand" }
    $after = Get-GitOut $OutDir rev-parse HEAD
    if ($Verify) { Invoke-FreshVerify $OutDir $sln }
    "ready: $OutDir, $($before.Substring(0,7)) -> $($after.Substring(0,7)) ($(Get-GitOut $OutDir rev-list --count "$before..HEAD") new commit(s) on top, nothing pushed)"
    return
}

# ---- 1. history ------------------------------------------------------------------------------------------------------------
$split = Get-GitOut $root subtree split -q -P $modulePath $SourceBranch
if ($split -notmatch '^[0-9a-f]{40}$') { throw "subtree split did not return a commit: $split" }
"split: $split"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
Invoke-Git $OutDir init -q -b main
# The new repository is not a worktree of this one: give it the same author identity (the commits are made by this script).
foreach ($k in 'user.name', 'user.email') { Invoke-Git $OutDir config $k (Get-GitOut $root config $k) }
Get-SplitCommit $OutDir $split
Invoke-Git $OutDir reset -q --hard FETCH_HEAD

# ---- 2. standalone layout: host contract as a submodule --------------------------------------------------------------------
Invoke-Git $OutDir submodule add -q $HostUrl external/rigbay
Invoke-Git $OutDir/external/rigbay checkout -q --detach $HostCommit
if (-not (Test-Path "$OutDir\external\rigbay\src\Rigbay.Abstractions\Rigbay.Abstractions.csproj")) { throw "the pinned host commit has no Rigbay.Abstractions" }
Invoke-Git $OutDir add external/rigbay .gitmodules

Update-HostPaths $OutDir
Invoke-Git $OutDir add -A
Invoke-Git $OutDir commit -q -m "Standalone layout: the host contract is the submodule external/rigbay ($($HostCommit.Substring(0,7)))" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"

# ---- 3. repository files ---------------------------------------------------------------------------------------------------
Write-Text "$OutDir\.gitignore" ("# build output`nbin/`nobj/`nTestResults/`n*.nupkg`n# editors and local files`n.vs/`n.vscode/`n*.user`n*.log`n")
Write-Text "$OutDir\LICENSE" ((Get-GitOut $root show "${SourceBranch}:LICENSE") + $nl)

$co = Action-Sha 'actions/checkout'
$sd = Action-Sha 'actions/setup-dotnet'
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

Needs Windows and the .NET SDK for ``$tfm`` or newer (the module targets ``$tfm``). The host contract (``Rigbay.Abstractions``) comes from the
git submodule ``external/rigbay``, so clone with submodules:

``````powershell
git clone --recurse-submodules https://github.com/rigbay-app/$RepoName.git
cd $RepoName
dotnet build $sln --configuration Release
dotnet test $sln --configuration Release
``````

The module folder is the build output of the module project (``$buildOut``).

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
    $hasDocs = (& git -C $root ls-tree -r --name-only $SourceBranch "docs/$Folder") -ne $null
    if ($hasDocs) {
        $zip = "$docsDir\docs.zip"
        & git -C $root archive --format=zip -o $zip $SourceBranch "docs/$Folder"
        if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
        Expand-Archive -LiteralPath $zip -DestinationPath $docsDir
        New-Item -ItemType Directory -Force "$OutDir\docs" | Out-Null
        Get-ChildItem "$docsDir\docs\$Folder" -Recurse -File | ForEach-Object {
            $name = $_.Name
            if ($ExcludeDocs | Where-Object { $name -like $_ }) { return }
            $dest = Join-Path "$OutDir\docs" $_.FullName.Substring("$docsDir\docs\$Folder\".Length)
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
Assert-NoPersonalData $OutDir
Invoke-Git $OutDir add -A
Invoke-Git $OutDir commit -q -m "README skeleton, LICENSE, .gitignore, documentation of the module copied from the host repository" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"

# ---- 5. optional: prove a fresh clone builds and tests ---------------------------------------------------------------------
if ($Verify) { Invoke-FreshVerify $OutDir $sln }
"ready: $OutDir (no remote, nothing pushed)"
