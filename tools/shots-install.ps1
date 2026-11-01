# Renders the Modules page in its install states (PrintWindow of the app's own HWND, never the screen) into tools\shots\install-*.png,
# against a SCRATCH registry and a SCRATCH modules folder per shot (tools\out\shots-install\<name>); nothing touches the real
# %ProgramData%\Rigbay\modules and nothing reaches GitHub. The scratch folders become admin-only (that is what the installer does),
# so they are removed at the end through tools\remove-elevated.ps1.
# Usage: .\shots-install.ps1 [name...]
[CmdletBinding(PositionalBinding = $false)]
param([string]$Out = (Join-Path $PSScriptRoot 'shots'), [Parameter(ValueFromRemainingArguments)][string[]]$Names)
$ErrorActionPreference = 'Stop'
$o = Join-Path $PSScriptRoot 'out'
$work = Join-Path $o 'shots-install'
New-Item -ItemType Directory -Force $Out | Out-Null
& "$PSScriptRoot\remove-elevated.ps1" $work | Out-Null
New-Item -ItemType Directory -Force $work | Out-Null
& "$PSScriptRoot\make-local-registry.ps1" | Out-Null
$reg = Join-Path $o 'local-registry\registry.json'
$mods = Join-Path $o 'local-registry\modules'

$all = 'page:modules;check;wait:700'
$restart = 'page:modules;check;install:demo-lighting;update:demo-fan;remove:demo-devices;wait:700'
$jobs = @(
    @{ n = 'modules-dark';           a = '--theme', 'dark',  '--size', '1240x1250', '--script', $all },
    @{ n = 'modules-light';          a = '--theme', 'light', '--size', '1240x1250', '--script', $all },
    @{ n = 'modules-vi-dark';        a = '--theme', 'dark',  '--size', '1240x1250', '--lang', 'vi', '--script', $all },
    @{ n = 'modules-progress-dark';  a = '--theme', 'dark',  '--size', '1240x1250', '--slow-download', '1500', '--script', 'page:modules;check;install-bg:demo-lighting;wait:600' },
    @{ n = 'modules-progress-light'; a = '--theme', 'light', '--size', '1240x1250', '--slow-download', '1500', '--script', 'page:modules;check;install-bg:demo-lighting;wait:600' },
    @{ n = 'modules-failed-dark';    a = '--theme', 'dark',  '--size', '1240x1250', '--script', 'page:modules;check;install:demo-tampered;wait:700' },
    @{ n = 'modules-failed-light';   a = '--theme', 'light', '--size', '1240x1250', '--script', 'page:modules;check;install:demo-tampered;wait:700' },
    @{ n = 'modules-restart-dark';   a = '--theme', 'dark',  '--size', '1240x1350', '--script', $restart },
    @{ n = 'modules-restart-light';  a = '--theme', 'light', '--size', '1240x1350', '--script', $restart },
    @{ n = 'modules-restart-vi-dark'; a = '--theme', 'dark', '--size', '1240x1350', '--lang', 'vi', '--script', $restart },
    @{ n = 'modules-details-dark';   a = '--theme', 'dark',  '--size', '1240x1500', '--script', 'page:modules;check;expand:available:demo-lighting;wait:600' },
    @{ n = 'modules-narrow-dark';    a = '--theme', 'dark',  '--size', '640x1100', '--script', $all },
    @{ n = 'modules-welcome-dark';   a = '--theme', 'dark',  '--size', '1240x800', '--first-run'; empty = $true },
    @{ n = 'modules-welcome-light';  a = '--theme', 'light', '--size', '1240x800', '--first-run'; empty = $true },
    @{ n = 'modules-offline-none-dark'; a = '--theme', 'dark', '--size', '1240x800', '--script', 'page:modules;check;wait:900'; registry = 'http://127.0.0.1:1/registry.json'; empty = $true },
    @{ n = 'settings-install-dark';  a = '--theme', 'dark',  '--size', '1240x900', '--script', 'page:settings' }
)
$only = @($Names)
if ($only.Count -gt 0) { $jobs = $jobs | Where-Object { $only -contains $_.n } }
foreach ($j in $jobs) {
    $dir = Join-Path $work $j.n
    New-Item -ItemType Directory -Force $dir | Out-Null
    $m = Join-Path $dir 'modules'
    if ($j.empty) { New-Item -ItemType Directory -Force $m | Out-Null } else { Copy-Item $mods $m -Recurse }
    $png = Join-Path $Out ('install-' + $j.n + '.png')
    Remove-Item $png -ErrorAction SilentlyContinue
    $registry = if ($j.registry) { $j.registry } else { $reg }
    $args2 = @('--registry', $registry, '--shot', $png, '--exit-after', '200', '--allow-multi') + $j.a
    & "$PSScriptRoot\run-elevated.ps1" -Wait -Data (Join-Path $dir 'data') -Modules $m @args2 | Out-Null
    '{0}: {1}' -f $j.n, (Test-Path $png)
}
& "$PSScriptRoot\remove-elevated.ps1" $work
