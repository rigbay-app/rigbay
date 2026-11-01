# Command-line verbs and the langcheck switch. Each call is one elevated Rigbay.App.exe (the manifest asks for it); the result comes
# back through --out because a GUI-subsystem exe cannot be waited on for console output from every shell.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'out\app\Rigbay.App.exe'
$script:mods = Join-Path $PSScriptRoot 'out\modules'
$data = Join-Path $PSScriptRoot 'out\data-cli'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
$script:fail = 0

function Call([string[]]$a, [string]$name, [int]$wantExit, [string]$wantText = '') {
    $f = Join-Path $PSScriptRoot 'out\cli-result.txt'
    Remove-Item $f -ErrorAction SilentlyContinue
    $all = @('--data', "`"$data`"", '--modules', "`"$($script:mods)`"", '--out', "`"$f`"") + $a
    $p = Start-Process $exe -ArgumentList $all -Wait -PassThru -Verb RunAs
    $text = if (Test-Path $f) { (Get-Content $f -Raw -Encoding utf8).Trim() } else { '' }
    $ok = ($p.ExitCode -eq $wantExit) -and ($wantText -eq '' -or $text.Contains($wantText))
    if (-not $ok) { $script:fail++ }
    "{0} {1}: exit {2} (want {3}) | {4}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $p.ExitCode, $wantExit, ($text -replace "`r?`n", ' / ')
}

# turn demo-fan on once, so a later start knows the user had it enabled
& "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules $script:mods --allow-multi --script 'enable:demo-fan' --exit-after 300 | Out-Null

'--- headless (no running instance): the exe loads the modules, runs the verb, stops them'
Call @('list') 'list' 0 'demo-fan'
Call @('version') 'version' 0 'Host API'
Call @('demo-fan', 'status') 'demo-fan status' 0 'mode=auto'
Call @('demo-fan', 'help') 'demo-fan help (lists the verbs, exit 2 as the contract says for usage)' 2 'demo-fan status'
Call @('demo-fan', 'max') 'demo-fan max (nobody can approve, so it is refused)' 1 'Not confirmed'
Call @('demo-fan', 'nope') 'unknown verb' 2 'no verb'
Call @('nope', 'x') 'unknown module' 2 'No module'
Call @('demo-lighting', 'x') 'module that is not running' 2 'not running'

'--- forwarded to a running instance (its state is changed first; a headless run would still say mode=auto)'
& "$PSScriptRoot\run-elevated.ps1" -Data $data -Modules $script:mods --script 'approve:true;page:demo-fan;edit:row.mode.header=max' --exit-after 40000 | Out-Null
Start-Sleep -Seconds 8
Call @('demo-fan', 'status') 'demo-fan status against the running instance' 0 'mode=max'
Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', 'Get-Process Rigbay.App -ErrorAction SilentlyContinue | Stop-Process -Force'

'--- langcheck'
Call @('--langcheck') 'langcheck, host and demo modules' 0 'langcheck: OK'
$bad = Join-Path $PSScriptRoot 'out\modules-badlang'
if (Test-Path $bad) { Remove-Item $bad -Recurse -Force }
New-Item -ItemType Directory -Force $bad | Out-Null
Copy-Item (Join-Path $script:mods 'demo-fan') (Join-Path $bad 'demo-fan') -Recurse
Add-Content (Join-Path $bad 'demo-fan\lang\vi.lang') 'module.demo-fan.row.typo.header = Loi go' -Encoding utf8
Add-Content (Join-Path $bad 'demo-fan\lang\vi.lang') 'module.demo-fan.state.line = Quat {0}' -Encoding utf8
$script:mods = $bad
Call @('--langcheck') 'langcheck finds a typo key and a placeholder mismatch' 1 'ERRORS'
"failed checks: $($script:fail)"
exit $script:fail
