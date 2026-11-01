# Runs an exe elevated (UAC auto-elevates for this admin account), waits, and leaves its console output in a file,
# because an elevated process cannot be read from a normal shell.
# Usage: .\run-elevated.ps1 -Exe <path> -Log <file> [-Args "a b c"]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Log,
    [string]$ProgramArgs = ''
)
$Exe = (Resolve-Path $Exe).Path
$Log = [IO.Path]::GetFullPath($Log)
if (Test-Path $Log) { Remove-Item $Log }
$cmd = "`"$Exe`" $ProgramArgs > `"$Log`" 2>&1"
$p = Start-Process cmd -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '/c', "`"$cmd`""
"exit code: $($p.ExitCode)"
if (Test-Path $Log) { Get-Content $Log }
