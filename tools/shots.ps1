# Renders the app window itself (PrintWindow on its own HWND, never the screen) into tools\shots\*.png, in the states
# the task lists: dashboard, empty, failed, unsupported, module page on and off, Vietnamese, in dark and light.
[CmdletBinding(PositionalBinding=$false)]
param([string]$Out = (Join-Path $PSScriptRoot 'shots'), [Parameter(ValueFromRemainingArguments)][string[]]$Names)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$fan = 'enable:demo-fan;wait:1800'
$jobs = @(
    @{ n = 'dashboard-dark';        a = '--theme', 'dark',  '--script', $fan },
    @{ n = 'dashboard-light';       a = '--theme', 'light', '--script', $fan },
    @{ n = 'empty-dark';            a = '--theme', 'dark',  '--modules', (Join-Path $PSScriptRoot 'out\modules-empty') },
    @{ n = 'empty-light';           a = '--theme', 'light', '--modules', (Join-Path $PSScriptRoot 'out\modules-empty') },
    @{ n = 'invalid-dark';          a = '--theme', 'dark',  '--script', 'page:demo-bad-manifest' },
    @{ n = 'invalid-light';         a = '--theme', 'light', '--script', 'page:demo-bad-manifest' },
    @{ n = 'invalid-vi-dark';       a = '--theme', 'dark',  '--lang', 'vi', '--script', 'page:demo-bad-manifest' },
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
    @{ n = 'revert-dark';           a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-light';          a = '--theme', 'light', '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-vi-dark';        a = '--theme', 'dark',  '--lang', 'vi', '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-button-dark';    a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.boost.header;wait:700;approve:ask;revert:row.boost.header;wait:900" },
    @{ n = 'unavailable-dark';      a = '--theme', 'dark',  '--size', '1240x1500', '--script', "$fan;page:demo-fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'unavailable-light';     a = '--theme', 'light', '--size', '1240x1500', '--script', "$fan;page:demo-fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-unavailable-dark';  a = '--theme', 'dark',  '--script', "$fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-unavailable-light'; a = '--theme', 'light', '--script', "$fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-vi-dark';     a = '--theme', 'dark',  '--lang', 'vi', '--script', $fan },
    @{ n = 'module-vi-dark';        a = '--theme', 'dark',  '--lang', 'vi', '--script', "$fan;page:demo-fan" },
    @{ n = 'module-vi-light';       a = '--theme', 'light', '--lang', 'vi', '--script', "$fan;page:demo-fan" },
    @{ n = 'modules-dark';          a = '--theme', 'dark',  '--script', "$fan;page:modules" },
    @{ n = 'modules-light';         a = '--theme', 'light', '--script', "$fan;page:modules" },
    @{ n = 'settings-dark';         a = '--theme', 'dark',  '--script', "$fan;page:settings" },
    @{ n = 'settings-light';        a = '--theme', 'light', '--script', "$fan;page:settings" },
    @{ n = 'log-dark';              a = '--theme', 'dark',  '--script', "$fan;page:log" },
    @{ n = 'modules-probe-dark';    a = '--theme', 'dark',  '--size', '1240x1150', '--script', "page:modules;expand:demo-devices;wait:500" },
    @{ n = 'modules-probe-light';   a = '--theme', 'light', '--size', '1240x1150', '--script', "page:modules;expand:demo-devices;wait:500" },
    @{ n = 'requires-dark';         a = '--theme', 'dark',  '--script', "page:demo-devices;turn-on:demo-devices;wait:900" },
    @{ n = 'requires-light';        a = '--theme', 'light', '--script', "page:demo-devices;turn-on:demo-devices;wait:900" },
    @{ n = 'refresh-1-dark';        a = '--theme', 'dark',  '--script', "enable:demo-devices;page:demo-devices;expand-all;wait:500" },
    @{ n = 'refresh-2-added-dark';  a = '--theme', 'dark',  '--script', "enable:demo-devices;page:demo-devices;expand-all;edit:row.add.header;wait:900;edit:row.add.header;wait:900" },
    @{ n = 'refresh-3-removed-dark';a = '--theme', 'dark',  '--script', "enable:demo-devices;page:demo-devices;expand-all;edit:row.add.header;wait:900;edit:row.remove.header;wait:600;edit:row.remove.header;wait:900" },
    @{ n = 'refresh-added-light';   a = '--theme', 'light', '--script', "enable:demo-devices;page:demo-devices;expand-all;edit:row.add.header;wait:900" },
    @{ n = 'refresh-scroll-dark';   a = '--theme', 'dark',  '--size', '1000x430', '--script', "enable:demo-devices;page:demo-devices;expand-all;edit:row.add.header;wait:700;edit:row.add.header;wait:700;edit:row.add.header;wait:700;scroll:9999;wait:300;edit:row.add.header;wait:900" },
    @{ n = 'module-on-tall-dark';   a = '--theme', 'dark',  '--size', '1240x1500', '--script', "$fan;page:demo-fan" },
    @{ n = 'narrow-modules-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:modules" },
    @{ n = 'narrow-settings-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:settings" },
    @{ n = 'narrow-log-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:log" },
@{ n = 'narrow-dark';           a = '--theme', 'dark',  '--size', '860x700', '--script', "$fan;page:demo-fan" }
)
$only = @($Names)
if ($only.Count -gt 0) { $jobs = $jobs | Where-Object { $only -contains $_.n } }
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
