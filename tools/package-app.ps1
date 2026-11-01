<#
.SYNOPSIS
  Builds the release zip of the Rigbay app: Rigbay-<version>-win-x64.zip.

.DESCRIPTION
  1. makes a clean clone of -Ref (default main) of -Repo (default: this repository) in a temp folder, so nothing from a working
     tree (untracked files, local edits, bin/obj) can get in;
  2. dotnet publish src/Rigbay.App -c Release -r win-x64 --self-contained false  (framework-dependent: needs .NET 10 and the
     Windows App Runtime 1.8; a self-contained build of 1.8 crashes at start, see docs/spike/SPIKE.md, so there is no such option);
  3. lays out the zip: app\ (no .pdb, no .xml docs), install.ps1, uninstall.ps1, LICENSE, README.txt, modules\ (empty);
  4. writes the zip deterministically (sorted names, fixed time = the commit time, fixed compression), prints size and SHA-256;
  5. checks the result: the exe has the manifest requireAdministrator, an icon and the version; no pdb, no absolute build path,
     no personal strings, no vendor files.

  Nothing is installed and nothing is sent anywhere.

.PARAMETER Ref
  Branch, tag or commit to package. Default: main.
.PARAMETER Repo
  Repository to clone: a path or URL. Default: the repository this script is in.
.PARAMETER Out
  Output folder. Default: tools\out\package.
.PARAMETER NoClone
  Package the working tree as it is (development only; the release uses the clean clone).
.PARAMETER KeepWork
  Keep the temp clone and publish folder (printed at the end).
#>
[CmdletBinding()]
param(
    [string]$Ref = 'main',
    [string]$Repo,
    [string]$Out,
    [switch]$NoClone,
    [switch]$KeepWork
)
$ErrorActionPreference = 'Stop'
$toolsDir = $PSScriptRoot
$thisRepo = Split-Path $toolsDir
if (-not $Repo) { $Repo = $thisRepo }
if (-not $Out) { $Out = Join-Path $toolsDir 'out\package' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

function Say($t) { Write-Host $t }
function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Fail($t) { throw $t }
function Run([string]$exe, [string[]]$a) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $exe @a 2>&1 | ForEach-Object { "$_" } | Write-Host
    $code = $LASTEXITCODE; $ErrorActionPreference = $old
    if ($code -ne 0) { Fail "$exe $($a -join ' ') failed ($code)" }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('rigbay-pkg-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # ---------------------------------------------------------------------------------------------------------- source
    Step 'Source'
    if ($NoClone) {
        $src = $thisRepo
        $commit = (& git -C $thisRepo rev-parse HEAD).Trim() + ' (working tree, not a clean clone)'
        $commitTime = [DateTimeOffset]::UtcNow
    }
    else {
        $src = Join-Path $work 'src'
        New-Item -ItemType Directory -Force $work | Out-Null
        Run 'git' @('clone', '--quiet', '--no-hardlinks', $Repo, $src)
        Run 'git' @('-C', $src, 'checkout', '--quiet', '--detach', $Ref)
        $dirty = & git -C $src status --porcelain
        if ($dirty) { Fail 'the clone is not clean' }
        $commit = (& git -C $src rev-parse HEAD).Trim()
        $commitTime = [DateTimeOffset]::Parse((& git -C $src log -1 --format=%cI).Trim())
    }
    Say "commit $commit"
    $props = [xml](Get-Content (Join-Path $src 'Directory.Build.props') -Raw)
    $version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "cannot read the version from Directory.Build.props ($version)" }
    Say "version $version"

    # --------------------------------------------------------------------------------------------------------- publish
    Step 'dotnet publish (framework-dependent, win-x64)'
    & (Join-Path $src 'tools\sync-brand-assets.ps1') -Check
    if ($LASTEXITCODE -ne 0) { Fail 'the app copies of the brand files differ from assets\brand' }
    $pub = Join-Path $work 'publish'
    # ContinuousIntegrationBuild maps source paths to /_/ so no absolute build path ends up in the binaries
    Run 'dotnet' @('publish', (Join-Path $src 'src\Rigbay.App\Rigbay.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        '-p:WindowsAppSDKSelfContained=false', '-p:ContinuousIntegrationBuild=true', '-o', $pub, '-v', 'q', '--nologo')

    # ------------------------------------------------------------------------------------------------------------ layout
    Step 'Layout'
    $stage = Join-Path $work 'stage'
    New-Item -ItemType Directory -Force (Join-Path $stage 'app'), (Join-Path $stage 'modules') | Out-Null
    $files = Get-ChildItem $pub -Recurse -File | Where-Object { $_.Extension -notin '.pdb', '.xml' -or $_.Name -like '*.deps.json' }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($pub.Length).TrimStart('\')
        $dest = Join-Path (Join-Path $stage 'app') $rel
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.FullName $dest
    }
    foreach ($n in 'install.ps1', 'uninstall.ps1') { Copy-Item (Join-Path $src "tools\installer\$n") (Join-Path $stage $n) }
    Copy-Item (Join-Path $src 'LICENSE') (Join-Path $stage 'LICENSE')
    (Get-Content (Join-Path $src 'tools\installer\README.txt') -Raw).Replace('@VERSION@', $version) | Set-Content (Join-Path $stage 'README.txt') -Encoding ASCII
    # a runtime check that the exe really is the one the layout needs
    foreach ($need in 'app\Rigbay.App.exe', 'app\Rigbay.App.dll', 'app\lang\en.lang', 'app\lang\vi.lang', 'app\Assets\rigbay.ico') {
        if (-not (Test-Path (Join-Path $stage $need))) { Fail "missing from the package: $need" }
    }

    # --------------------------------------------------------------------------------------------------------------- zip
    Step 'Zip'
    $zipName = "Rigbay-$version-win-x64.zip"
    $zipPath = Join-Path $Out $zipName
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # fixed time: the commit's UTC clock reading, rounded down to the 2 s a zip can hold (the zip format has no time zone)
    $utc = $commitTime.UtcDateTime
    $stamp = New-Object DateTimeOffset ([DateTime]::new($utc.Year, $utc.Month, $utc.Day, $utc.Hour, $utc.Minute, ($utc.Second - ($utc.Second % 2)), [DateTimeKind]::Unspecified)), ([TimeSpan]::Zero)
    # every entry as name -> source file (null for a folder); names sorted ordinally so the order does not depend on the culture
    $map = @{}
    foreach ($d in Get-ChildItem $stage -Recurse -Directory) { $map[$d.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/') + '/'] = $null }
    foreach ($f in Get-ChildItem $stage -Recurse -File) { $map[$f.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/')] = $f.FullName }
    $names = [string[]]@($map.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $fs = [IO.File]::Create($zipPath)
    try {
        $zip = New-Object IO.Compression.ZipArchive ($fs, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($name in $names) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $stamp
                if ($map[$name]) {
                    $in = [IO.File]::OpenRead($map[$name])
                    $o = $entry.Open()
                    try { $in.CopyTo($o) } finally { $o.Dispose(); $in.Dispose() }
                }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $fs.Dispose() }
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$hash *$zipName`n", (New-Object Text.UTF8Encoding $false))
    $sizeBytes = (Get-Item $zipPath).Length

    # ------------------------------------------------------------------------------------------------------------ checks
    Step 'Checks on the package'
    $check = Join-Path $work 'check'
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $check)
    $problems = @()
    $all = Get-ChildItem $check -Recurse -File
    $count = $all.Count
    Say "$count files; top level: $((Get-ChildItem $check | ForEach-Object { $_.Name }) -join ', ')"
    if ($all | Where-Object { $_.Extension -in '.pdb' }) { $problems += 'a .pdb file is in the zip' }
    if (@(Get-ChildItem (Join-Path $check 'modules') -Force).Count -ne 0) { $problems += 'modules folder is not empty' }

    $exe = Join-Path $check 'app\Rigbay.App.exe'
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PeRes
{
    delegate bool EnumProc(IntPtr h, IntPtr type, IntPtr name, IntPtr param);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string file, IntPtr h, uint flags);
    [DllImport("kernel32", SetLastError = true)] static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr LockResource(IntPtr res);
    [DllImport("kernel32", SetLastError = true)] static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("kernel32", SetLastError = true)] static extern bool EnumResourceNames(IntPtr h, IntPtr type, EnumProc proc, IntPtr param);

    const uint DataFile = 0x2, ImageResource = 0x20;

    /// <summary>The embedded application manifest (resource type 24, id 1) as text, or null.</summary>
    public static string Manifest(string file)
    {
        IntPtr h = LoadLibraryEx(file, IntPtr.Zero, DataFile | ImageResource);
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr r = FindResource(h, (IntPtr)1, (IntPtr)24);
            if (r == IntPtr.Zero) return null;
            IntPtr p = LockResource(LoadResource(h, r));
            uint n = SizeofResource(h, r);
            byte[] b = new byte[n];
            Marshal.Copy(p, b, 0, (int)n);
            return Encoding.UTF8.GetString(b);
        }
        finally { FreeLibrary(h); }
    }

    /// <summary>How many icon groups (resource type 14) the file has.</summary>
    public static int IconGroups(string file)
    {
        IntPtr h = LoadLibraryEx(file, IntPtr.Zero, DataFile | ImageResource);
        if (h == IntPtr.Zero) return -1;
        try
        {
            int count = 0;
            EnumResourceNames(h, (IntPtr)14, (hh, t, n, pp) => { count++; return true; }, IntPtr.Zero);
            return count;
        }
        finally { FreeLibrary(h); }
    }
}
'@
    $manifest = [PeRes]::Manifest($exe)
    if (-not $manifest) { $problems += 'the exe has no embedded manifest' }
    elseif ($manifest -notmatch 'requestedExecutionLevel\s+level="requireAdministrator"') { $problems += 'the manifest does not say requireAdministrator' }
    else { Say 'exe manifest: requestedExecutionLevel = requireAdministrator' }
    $icons = [PeRes]::IconGroups($exe)
    if ($icons -lt 1) { $problems += 'the exe has no icon' } else { Say "exe icon groups: $icons" }
    $vi = (Get-Item $exe).VersionInfo
    Say ("exe version: FileVersion {0}, ProductVersion {1}, ProductName {2}, FileDescription {3}" -f $vi.FileVersion, $vi.ProductVersion, $vi.ProductName, $vi.FileDescription)
    if ($vi.ProductVersion -ne $version) { $problems += "ProductVersion is '$($vi.ProductVersion)', expected '$version'" }
    if (-not $vi.FileVersion -or -not $vi.FileVersion.StartsWith($version)) { $problems += "FileVersion is '$($vi.FileVersion)', expected it to start with '$version'" }
    if ($vi.ProductName -ne 'Rigbay') { $problems += "ProductName is '$($vi.ProductName)'" }

    # absolute paths and personal strings, in every file (ASCII and UTF-16 spellings)
    $needles = New-Object System.Collections.Generic.List[string]
    foreach ($n in $work, $src, $env:USERNAME, $env:COMPUTERNAME, $env:USERPROFILE, 'C:\Users\', 'D:\Documents', 'setup-token') { if ($n -and $n.Length -ge 4) { [void]$needles.Add($n) } }
    $needles = @($needles | Select-Object -Unique)
    $hits = @()
    foreach ($f in $all) {
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $ascii = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        $wide = [Text.Encoding]::Unicode.GetString($bytes)
        $wide2 = if ($bytes.Length -gt 1) { [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) } else { '' }
        foreach ($n in $needles) {
            foreach ($text in $ascii, $wide, $wide2) {
                if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits += "$($f.FullName.Substring($check.Length).TrimStart('\')): contains '$n'"; break }
            }
        }
    }
    if ($hits.Count -gt 0) { $problems += $hits }
    else { Say "no build path, user name, machine name or personal path in any of the $count files" }
    # nothing of a vendor in the zip (names only: the app has no reason to carry these)
    $vendor = $all | Where-Object { $_.Name -match '(?i)^(Xtu|Acer|Predator|Intel|PSSvc)' }
    if ($vendor) { $problems += ('vendor-looking files: ' + (($vendor | ForEach-Object Name) -join ', ')) } else { Say 'no vendor-named files' }
    # the scripts parse
    foreach ($s in 'install.ps1', 'uninstall.ps1') {
        $errs = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $check $s), [ref]$null, [ref]$errs)
        if ($errs.Count -gt 0) { $problems += "$s does not parse: $($errs[0].Message)" } else { Say "$s parses" }
    }
    # only ASCII in the scripts (they run in Windows PowerShell 5.1 without a BOM)
    foreach ($s in 'install.ps1', 'uninstall.ps1', 'README.txt') { if ([IO.File]::ReadAllBytes((Join-Path $check $s)) | Where-Object { $_ -gt 127 } | Select-Object -First 1) { $problems += "$s has a non-ASCII character" } }

    if ($problems.Count -gt 0) {
        Write-Host ''
        $problems | ForEach-Object { Write-Host "PROBLEM: $_" -ForegroundColor Red }
        Fail "$($problems.Count) problem(s); the zip at $zipPath must not be released"
    }

    Step 'Result'
    Say "zip:    $zipPath"
    Say ("size:   {0:N0} bytes ({1:N1} MB)" -f $sizeBytes, ($sizeBytes / 1MB))
    Say "sha256: $hash"
    Say "commit: $commit"
    Say "files:  $count"
    Say 'all checks passed'
}
finally {
    if ($KeepWork) { Say "work folder kept: $work" }
    elseif (Test-Path $work) {
        # a clean clone has read-only .git objects
        Get-ChildItem $work -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

