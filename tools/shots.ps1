# Renders the app window itself (PrintWindow on its own HWND, never the screen) into tools\shots\*.png, in the states
# the task lists: dashboard, empty, failed, unsupported, module page on and off, Vietnamese, in dark and light.
param([string]$Out = (Join-Path $PSScriptRoot 'shots'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$fan = 'enable:demo-fan;wait:1800'
$jobs = @(
    @{ n = 'dashboard-dark';        a = '--theme', 'dark',  '--script', $fan },
    @{ n = 'dashboard-light';       a = '--theme', 'light', '--script', $fan },
    @{ n = 'empty-dark';            a = '--theme', 'dark',  '--modules', (Join-Path $PSScriptRoot 'out\modules-empty') },
    @{ n = 'empty-light';           a = '--theme', 'light', '--modules', (Join-Path $PSScriptRoot 'out\modules-empty') },
    @{ n = 'failed-dark';           a = '--theme', 'dark',  '--script', 'enable:demo-broken;page:demo-broken' },
    @{ n = 'failed-light';          a = '--theme', 'light', '--script', 'enable:demo-broken;page:demo-broken' },
    @{ n = 'unsupported-dark';      a = '--theme', 'dark',  '--script', 'page:demo-lighting' },
    @{ n = 'unsupported-light';     a = '--theme', 'light', '--script', 'page:demo-lighting' },
    @{ n = 'incompatible-dark';     a = '--theme', 'dark',  '--script', 'page:demo-future' },
    @{ n = 'module-on-dark';        a = '--theme', 'dark',  '--script', "$fan;page:demo-fan" },
    @{ n = 'module-on-light';       a = '--theme', 'light', '--script', "$fan;page:demo-fan" },
    @{ n = 'module-off-dark';       a = '--theme', 'dark',  '--script', "$fan;disable:demo-fan;page:demo-fan" },
    @{ n = 'module-off-light';      a = '--theme', 'light', '--script', "$fan;disable:demo-fan;page:demo-fan" },
    @{ n = 'module-never-on-dark';  a = '--theme', 'dark',  '--script', 'page:demo-fan' },
    @{ n = 'approval-dark';         a = '--theme', 'dark',  '--script', "$fan;page:demo-fan;edit:row.mode.header=max;wait:900" },
    @{ n = 'approval-light';        a = '--theme', 'light', '--script', "$fan;page:demo-fan;edit:row.mode.header=max;wait:900" },
    @{ n = 'applied-dark';          a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;edit:row.pl1.header=60;wait:300" },
    @{ n = 'dashboard-vi-dark';     a = '--theme', 'dark',  '--lang', 'vi', '--script', $fan },
    @{ n = 'module-vi-dark';        a = '--theme', 'dark',  '--lang', 'vi', '--script', "$fan;page:demo-fan" },
    @{ n = 'module-vi-light';       a = '--theme', 'light', '--lang', 'vi', '--script', "$fan;page:demo-fan" },
    @{ n = 'modules-dark';          a = '--theme', 'dark',  '--script', "$fan;page:modules" },
    @{ n = 'modules-light';         a = '--theme', 'light', '--script', "$fan;page:modules" },
    @{ n = 'settings-dark';         a = '--theme', 'dark',  '--script', "$fan;page:settings" },
    @{ n = 'settings-light';        a = '--theme', 'light', '--script', "$fan;page:settings" },
    @{ n = 'log-dark';              a = '--theme', 'dark',  '--script', "$fan;page:log" },
    @{ n = 'module-on-tall-dark';   a = '--theme', 'dark',  '--size', '1240x1500', '--script', "$fan;page:demo-fan" },
    @{ n = 'narrow-dark';           a = '--theme', 'dark',  '--size', '860x700', '--script', "$fan;page:demo-fan" }
)
if ($args.Count -gt 0) { $jobs = $jobs | Where-Object { $args -contains $_.n } }
foreach ($j in $jobs) {
    $png = Join-Path $Out ($j.n + '.png')
    Remove-Item $png -ErrorAction SilentlyContinue
    $run = @{ Wait = $true }
    $extra = @('--shot', $png, '--exit-after', '200', '--allow-multi') + $j.a
    # a fresh data folder per shot, so enabled/disabled choices never leak between states
    $data = Join-Path $PSScriptRoot ('out\data-shot-' + $j.n)
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    $mods = $null
    for ($i = 0; $i -lt $extra.Count; $i++) { if ($extra[$i] -eq '--modules') { $mods = $extra[$i + 1]; $extra = $extra[0..($i - 1)] + $(if ($i + 2 -lt $extra.Count) { $extra[($i + 2)..($extra.Count - 1)] } else { @() }); break } }
    if ($mods) { & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules $mods @extra | Out-Null }
    else { & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data @extra | Out-Null }
    "{0}: {1}" -f $j.n, (Test-Path $png)
}
