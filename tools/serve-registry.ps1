# Serves a local registry over real HTTP on the loopback address, for checking the app's download path (ETag, redirects, streaming) without GitHub.
#   <Dir>\registry.json  at  http://127.0.0.1:<Port>/registry.json   (answers If-None-Match with 304)
#   <Dir>\zips\*.zip     at  http://127.0.0.1:<Port>/zips/<name>
# Build the registry for it with:  make-local-registry.ps1 -BaseUrl http://127.0.0.1:<Port>/zips/
# The app only accepts such a registry with scratch --data and --modules and --registry http://127.0.0.1:<Port>/registry.json
# Usage: .\serve-registry.ps1 [-Dir tools\out\local-registry] [-Port 18765] [-Seconds 120] [-LogFile <file>]   (stops by itself after -Seconds)
param(
    [string]$Dir = (Join-Path $PSScriptRoot 'out\local-registry'),
    [int]$Port = 18765,
    [int]$Seconds = 120,
    [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
function Log($m) { $line = '{0:HH:mm:ss.fff} {1}' -f (Get-Date), $m; Write-Host $line; if ($LogFile) { Add-Content -Path $LogFile -Value $line } }
Log "serving $Dir on http://127.0.0.1:$Port/ for $Seconds s"
$end = (Get-Date).AddSeconds($Seconds)
$etag = '"' + (Get-FileHash (Join-Path $Dir 'registry.json') -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant() + '"'
while ((Get-Date) -lt $end) {
    $task = $listener.GetContextAsync()
    while (-not $task.IsCompleted -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 100 }
    if (-not $task.IsCompleted) { break }
    $ctx = $task.Result
    $req = $ctx.Request; $res = $ctx.Response
    $path = $req.Url.AbsolutePath
    $inm = $req.Headers['If-None-Match']
    $ua = $req.UserAgent
    $headers = ($req.Headers.AllKeys | ForEach-Object { $_ }) -join ','
    try {
        if ($path -eq '/registry.json') {
            if ($inm -eq $etag) { $res.StatusCode = 304; Log "GET $path -> 304 (If-None-Match $inm) ua=$ua headers=[$headers]" }
            else {
                $bytes = [IO.File]::ReadAllBytes((Join-Path $Dir 'registry.json'))
                $res.StatusCode = 200; $res.ContentType = 'application/json'; $res.Headers.Add('ETag', $etag); $res.ContentLength64 = $bytes.Length
                $res.OutputStream.Write($bytes, 0, $bytes.Length)
                Log "GET $path -> 200 ($($bytes.Length) bytes) ua=$ua headers=[$headers]"
            }
        }
        elseif ($path -like '/zips/*' -and (Test-Path (Join-Path $Dir ('zips\' + [IO.Path]::GetFileName($path))))) {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $Dir ('zips\' + [IO.Path]::GetFileName($path))))
            $res.StatusCode = 200; $res.ContentType = 'application/zip'; $res.ContentLength64 = $bytes.Length
            # in slices, so the client sees progress
            for ($o = 0; $o -lt $bytes.Length; $o += 16384) { $n = [Math]::Min(16384, $bytes.Length - $o); $res.OutputStream.Write($bytes, $o, $n); $res.OutputStream.Flush(); Start-Sleep -Milliseconds 150 }
            Log "GET $path -> 200 ($($bytes.Length) bytes) headers=[$headers]"
        }
        else { $res.StatusCode = 404; Log "GET $path -> 404" }
    }
    catch { Log "error on ${path}: $($_.Exception.Message)" }
    finally { try { $res.Close() } catch { } }
}
$listener.Stop()
Log 'stopped'
