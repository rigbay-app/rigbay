# Deletes folders that Rigbay's installer made admin-only (the self-test and the screenshots install into scratch folders under tools\out).
# A normal shell cannot change or delete them; this asks for an elevated PowerShell (UAC auto-elevates for an admin account), waits, reports.
# Usage: .\remove-elevated.ps1 <folder> [<folder> ...]
param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Path)
$ErrorActionPreference = 'Stop'
$todo = @($Path | ForEach-Object { [IO.Path]::GetFullPath($_) } | Where-Object { Test-Path $_ })
if ($todo.Count -eq 0) { 'nothing to delete'; return }
foreach ($p in $todo) {
    # refuse anything that is not under tools\out: this script exists for scratch folders only
    $out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'out')) + [IO.Path]::DirectorySeparatorChar
    if (-not $p.StartsWith($out, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to delete '$p': only folders under $out" }
}
$cmd = ($todo | ForEach-Object { "Remove-Item -LiteralPath '$($_.Replace("'", "''"))' -Recurse -Force -ErrorAction SilentlyContinue" }) -join '; '
$p = Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $cmd
$left = @($todo | Where-Object { Test-Path $_ })
if ($left.Count -gt 0) { throw "could not delete: $($left -join ', ')" }
"deleted: $($todo -join ', ')"
