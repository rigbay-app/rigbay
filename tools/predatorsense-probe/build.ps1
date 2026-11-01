# Builds predatorsense-probe.exe with the .NET Framework compiler that ships with Windows (no SDK needed).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
& $csc /nologo /target:exe /platform:anycpu /optimize+ `
    /reference:System.Management.dll /reference:System.ServiceProcess.dll `
    /out:"$bin\predatorsense-probe.exe" "$here\Probe.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "built $bin\predatorsense-probe.exe"
