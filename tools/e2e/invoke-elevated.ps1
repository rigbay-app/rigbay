# Runs a PowerShell script in an ELEVATED Windows PowerShell (UAC auto-elevates for this admin account), waits, and prints what it wrote.
# The app is elevated, so the process that drives its window through UI Automation has to be elevated too (UIPI).
# Usage: .\invoke-elevated.ps1 -File <script.ps1> [-Arguments '-Root','x'] [-TimeoutSec 900]
# Arguments that look like parameter names (-Root) are passed as names, everything else as a quoted string.
# The script's output (all streams, UTF-8) goes to tools\out\e2e\<script>.out.txt and is printed here at the end.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$File,
    [string[]]$Arguments = @(),
    [int]$TimeoutSec = 900,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
$File = (Resolve-Path $File).Path
if (-not $OutFile) {
    $outDir = Join-Path $PSScriptRoot '..\out\e2e'   # tools\out is git-ignored; the driver's own folder is not
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $OutFile = Join-Path ([IO.Path]::GetFullPath($outDir)) ([IO.Path]::GetFileNameWithoutExtension($File) + '.out.txt')
}
Remove-Item -LiteralPath $OutFile -ErrorAction SilentlyContinue
$q = { param($s) "'" + $s.Replace("'", "''") + "'" }
$quoted = ($Arguments | ForEach-Object { if ($_ -match '^-[A-Za-z]+$') { $_ } else { & $q $_ } }) -join ' '
$cmd = "`$c = 0; try { & $(& $q $File) $quoted *>&1 | Out-File -LiteralPath $(& $q $OutFile) -Encoding utf8 -Width 4000; `$c = `$LASTEXITCODE } catch { (`$_ | Out-String) | Add-Content -LiteralPath $(& $q $OutFile) -Encoding utf8; `$c = 1 }; exit `$c"
$p = Start-Process powershell -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd
if (-not $p.WaitForExit($TimeoutSec * 1000)) { Write-Host "timed out after $TimeoutSec s (the elevated script may still run: tools\stop-elevated.ps1)"; exit 124 }
if (Test-Path -LiteralPath $OutFile) { Get-Content -LiteralPath $OutFile -Encoding UTF8 }
exit $p.ExitCode
