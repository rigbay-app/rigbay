# Starts the spike host elevated (UAC auto-elevates for an admin account; the manifest asks for it anyway).
# Usage: .\run-elevated.ps1 [-Variant fd|sc] [-Wait] [host args...]
#   host args: --log <file> --shot <png> [--shot-mode printwindow|rtb] --theme dark|light --lang en|vi
#              --selftest --exit-after <ms> --modules <dir> --allow-multi
param(
    [ValidateSet('fd', 'sc')][string]$Variant = 'fd',
    [switch]$Wait,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$HostArgs
)
$exe = Join-Path $PSScriptRoot "out\host-$Variant\Rigbay.Host.exe"
if (-not (Test-Path $exe)) { throw "Not built: $exe (run .\build.ps1)" }
$p = @{ FilePath = $exe; Verb = 'RunAs'; PassThru = $true; WorkingDirectory = (Split-Path $exe) }
if ($HostArgs) { $p.ArgumentList = $HostArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } } }
if ($Wait) { $p.Wait = $true }
$proc = Start-Process @p
if ($Wait) { "exit code: $($proc.ExitCode)" } else { "started pid $($proc.Id)" }
