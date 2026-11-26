# W5 keyboard lighting writes (owner approved in chat on 2026-11-13: "W5a: 100 -> 75 -> 100" and "W5b: zone 1 red -> Cyan").
# ONLY the four calls below exist; the values are constants and cannot be given on the command line. One call per run.
# Needs an elevated process. Every run appends to the log next to this script. See docs/predatorsense/WRITE-PROPOSALS.md W5.
param([Parameter(Mandatory)][ValidateSet('W5a1', 'W5a2', 'W5b1', 'W5b2')][string]$Step, [string]$Log)
$ErrorActionPreference = 'Stop'
if (-not $Log) { $Log = Join-Path $PSScriptRoot 'lighting-w5.log' }
function Note([string]$text) { $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + " $Step $text"; Add-Content -Path $Log -Value $line -Encoding UTF8; $line }

# 16 bytes: byte 0 effect (0 = static), byte 1 speed, byte 2 brightness percent, the rest zero (the tail is zeros: our choice, FINDINGS 16.4).
function Backlight([byte]$pct) { $b = New-Object byte[] 16; $b[2] = $pct; , $b }
$calls = @{
    'W5a1' = @{ Method = 'SetGamingKBBacklight'; Value = (Backlight 75);  Text = 'static, brightness 75 % (00 00 4B 00 ... 16 bytes)' }
    'W5a2' = @{ Method = 'SetGamingKBBacklight'; Value = (Backlight 100); Text = 'static, brightness 100 % (00 00 64 00 ... 16 bytes), undo of W5a1' }
    # PowerShell 5.1 reads 0xC7AE0001 as a negative Int32, so the values are parsed from text.
    'W5b1' = @{ Method = 'SetGamingRgbKb';       Value = [Convert]::ToUInt32('0000FF01', 16); Text = 'zone 1 red #FF0000 (0x0000FF01)' }
    'W5b2' = @{ Method = 'SetGamingRgbKb';       Value = [Convert]::ToUInt32('C7AE0001', 16); Text = 'zone 1 Cyan #00AEC7 (0xC7AE0001), undo of W5b1' }
}
$c = $calls[$Step]
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) { Note 'REFUSED not elevated'; exit 2 }
Note "START AcerGamingFunction.$($c.Method) $($c.Text)"
try {
    $inst = Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcerGamingFunction' -OperationTimeoutSec 15 | Select-Object -First 1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Note 'WRITE_ATTEMPT'
    $r = Invoke-CimMethod -InputObject $inst -MethodName $c.Method -Arguments @{ gmInput = $c.Value } -OperationTimeoutSec 15
    $raw = [uint64]$r.gmOutput
    Note ("WRITE_RESULT raw=0x{0:X} status={1} elapsed_ms={2}" -f $raw, ($raw -band 0xFF), $sw.ElapsedMilliseconds)
    if (($raw -band 0xFF) -ne 0) { Note 'ALERT non-zero status; nothing else is sent'; exit 1 }
    exit 0
}
catch { Note ("WRITE_ERROR " + $_.Exception.Message); exit 1 }
