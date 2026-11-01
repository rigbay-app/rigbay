# Real-HTTP check of install, in SCRATCH folders only (tools\out\install-http; never the real ProgramData modules folder, never GitHub):
#   1. a local registry served over http://127.0.0.1:<Port> by tools\serve-registry.ps1 (a real socket, ETag, a slow zip)
#   2. the app (elevated, scratch --data and --modules, --registry) checks the list twice (the second request is conditional: 304),
#      installs demo-lighting through the real downloader, SHA-256 verified, queued for the restart
#   3. the app is started again (what a restart does): the start-up swap puts the module in place, turned off, and the page says so
#   4. the server is gone: the app starts, "Check for updates" says offline and shows the saved list
#   5. icacls of the scratch modules folder and install folder, from an elevated shell (the real DACL the installer set)
# Evidence: tools\shots\install-http-*.png, tools\shots\install-http-server.log, tools\shots\install-http-icacls.txt, the app logs.
param([int]$Port = 18765)
$ErrorActionPreference = 'Stop'
$o = Join-Path $PSScriptRoot 'out'
$shots = Join-Path $PSScriptRoot 'shots'
$base = Join-Path $o 'install-http'
New-Item -ItemType Directory -Force $shots | Out-Null
& "$PSScriptRoot\remove-elevated.ps1" $base | Out-Null
New-Item -ItemType Directory -Force $base | Out-Null
$reg = Join-Path $base 'registry'
& "$PSScriptRoot\make-local-registry.ps1" -Out $reg -BaseUrl "http://127.0.0.1:$Port/zips/" | Out-Null
$mods = Join-Path $base 'modules'
Copy-Item (Join-Path $reg 'modules') $mods -Recurse
$data = Join-Path $base 'data'
$url = "http://127.0.0.1:$Port/registry.json"
$serverLog = Join-Path $shots 'install-http-server.log'
Remove-Item $serverLog -ErrorAction SilentlyContinue

function Run-App([string]$name, [string[]]$more) {
    $png = Join-Path $shots "install-http-$name.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules $mods --registry $url --theme dark --size 1240x1250 --shot $png --exit-after 200 --allow-multi @more | Out-Null
    "{0}: {1}" -f $name, (Test-Path $png)
}

# --- 1 + 2 + 3: server up
$server = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'serve-registry.ps1'), '-Dir', $reg, '-Port', $Port, '-Seconds', 75, '-LogFile', $serverLog
Start-Sleep -Seconds 2
Run-App 'installed-queued' @('--script', 'page:modules;check;check;install:demo-lighting;wait:600')
Run-App 'after-restart' @('--script', 'page:modules;wait:600')
$server.WaitForExit(90000) | Out-Null

# --- 4: server gone
Run-App 'offline-cached' @('--script', 'page:modules;check;wait:900')

# --- 5: the real permissions
$icacls = Join-Path $shots 'install-http-icacls.txt'
Remove-Item $icacls -ErrorAction SilentlyContinue
$dump = @"
`$out = @()
foreach (`$p in '$mods', '$mods.install', '$mods\demo-lighting', '$mods.install\registry') { `$out += '== ' + `$p; `$out += (icacls `$p) }
`$out | Out-File -Encoding utf8 '$icacls'
"@
$ps1 = Join-Path $base 'dump-acl.ps1'
Set-Content -Path $ps1 -Value $dump -Encoding UTF8
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ps1
Get-Content $icacls
'--- server log'
Get-Content $serverLog
'--- app log (install lines)'
Get-Content (Join-Path $data 'logs\rigbay.log') | Select-String 'install|registry|demo-lighting'
& "$PSScriptRoot\remove-elevated.ps1" $base
