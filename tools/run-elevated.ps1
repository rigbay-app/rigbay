# Starts Rigbay.App elevated (the manifest asks for it; UAC auto-elevates for an admin account) with its own data folder
# and the demo modules, so nothing touches your real %LOCALAPPDATA%\Rigbay or the real modules folder.
# Usage: .\run-elevated.ps1 [-Wait] [-Modules <dir>] [-Data <dir>] [app args...]
#   app args: --tray --theme dark|light --lang en|vi --page <id|dashboard|modules|settings|log> --script "enable:demo-fan;page:demo-fan"
#             --shot <png> --size 1240x800 --selftest --selftest-out <file> --exit-after <ms> --allow-multi
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Wait,
    [string]$Modules = (Join-Path $PSScriptRoot 'out\modules'),
    [string]$Data = (Join-Path $PSScriptRoot 'out\data'),
    [string]$AppDir,   # folder with Rigbay.App.exe; default tools\out\app (for example the app\ folder of an extracted release zip)
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs
)
$exe = if ($AppDir) { Join-Path $AppDir 'Rigbay.App.exe' } else { Join-Path $PSScriptRoot 'out\app\Rigbay.App.exe' }
if (-not (Test-Path $exe)) { throw "Not built: $exe (run .\build.ps1)" }
New-Item -ItemType Directory -Force $Data | Out-Null
$all = @('--data', $Data, '--modules', $Modules) + $AppArgs
$p = @{ FilePath = $exe; Verb = 'RunAs'; PassThru = $true; WorkingDirectory = (Split-Path $exe) }
$p.ArgumentList = $all | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
if ($Wait) { $p.Wait = $true }
$proc = Start-Process @p
if ($Wait) { "exit code: $($proc.ExitCode)"; $proc.ExitCode } else { "started pid $($proc.Id)" }
