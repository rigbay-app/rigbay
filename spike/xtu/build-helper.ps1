# Builds the net48 XTU helper (design B) with the C# compiler that ships with Windows. No SDK needed.
# Intel's contract DLLs are referenced in place and are NOT copied next to the exe.
$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$xtu = 'C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers'
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'XtuHelper.exe'
& $csc /nologo /target:exe /platform:x64 /optimize+ /warn:4 "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.ServiceModel.dll /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll `
    "/r:$xtu\XtuInterface.dll" "/r:$xtu\XtuCommon.dll" `
    (Join-Path $PSScriptRoot 'helper\XtuHelper.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
$f = Get-Item $exe
"Built $exe ($($f.Length) bytes); files next to it: " + ((Get-ChildItem $out -File | % Name) -join ', ')
