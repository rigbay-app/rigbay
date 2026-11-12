# Renders the app window itself (PrintWindow on its own HWND, never the screen) into tools\shots\*.png, in the states
# the task lists: dashboard, empty, failed, unsupported, module page on and off, Vietnamese, in dark and light.
# Every shot runs against neutral scratch folders (default C:\Rigbay-demo: a copy of the demo modules, one data folder per shot), so no
# developer path, user name or machine name reaches a PNG: the Settings page and the log print the folders they use.
# The app makes the scratch folders admin-only; remove them afterwards with tools\remove-elevated.ps1-style elevated delete.
[CmdletBinding(PositionalBinding=$false)]
param([string]$Out = '', [string]$Demo = 'C:\Rigbay-demo', [Parameter(ValueFromRemainingArguments)][string[]]$Names)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'shots' }   # not in the param default: $PSScriptRoot is empty there on Windows PowerShell 5.1
New-Item -ItemType Directory -Force $Out | Out-Null
$demoModules = Join-Path $Demo 'modules'
$demoEmpty = Join-Path $Demo 'modules-empty'
if (Test-Path $demoModules) { Remove-Item $demoModules -Recurse -Force }
New-Item -ItemType Directory -Force $Demo | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'out\modules') $demoModules -Recurse
New-Item -ItemType Directory -Force $demoEmpty | Out-Null
# the README hero: only well-behaved demo modules, no failed or broken rows
$demoHero = Join-Path $Demo 'modules-hero'
if (Test-Path $demoHero) { Remove-Item $demoHero -Recurse -Force }
New-Item -ItemType Directory -Force $demoHero | Out-Null
foreach ($m in 'demo-fan', 'demo-devices', 'demo-lighting') { Copy-Item (Join-Path $demoModules $m) (Join-Path $demoHero $m) -Recurse }
$fan = 'enable:demo-fan;wait:1800'
# V1 (readings made visual): the metrics clock runs 6 times faster (--time-scale 6), so 52 s of waiting is about five minutes of history, and the
# tiles, sparklines and the 5-minute chart show what they show after five real minutes. The demo modules publish made-up numbers, nothing is read from this machine.
$ts = '--time-scale', '6'
$warm = 'wait:52000'
$visFan = "enable:demo-fan;page:demo-fan;$warm"
$visDash = 'enable:demo-fan;wait:40000'
$jobs = @(
    @{ n = 'dashboard-dark';        a = ('--theme', 'dark') + $ts + ('--script', $visDash) },
    @{ n = 'hero-dark';             a = ('--theme', 'dark',  '--modules', $demoHero, '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'hero-dashboard-dark';   a = ('--theme', 'dark',  '--modules', $demoHero, '--size', '1240x520') + $ts + ('--script', "enable:demo-fan;enable:demo-devices;wait:40000") },
    @{ n = 'dashboard-light';       a = ('--theme', 'light') + $ts + ('--script', $visDash) },
    @{ n = 'empty-dark';            a = '--theme', 'dark',  '--modules', $demoEmpty },
    @{ n = 'empty-light';           a = '--theme', 'light', '--modules', $demoEmpty },
    @{ n = 'invalid-dark';          a = '--theme', 'dark',  '--script', 'page:demo-bad-manifest' },
    @{ n = 'invalid-light';         a = '--theme', 'light', '--script', 'page:demo-bad-manifest' },
    @{ n = 'invalid-vi-dark';       a = '--theme', 'dark',  '--lang', 'vi', '--script', 'page:demo-bad-manifest' },
    @{ n = 'failed-dark';           a = '--theme', 'dark',  '--script', 'enable:demo-broken;page:demo-broken' },
    @{ n = 'failed-light';          a = '--theme', 'light', '--script', 'enable:demo-broken;page:demo-broken' },
    @{ n = 'unsupported-dark';      a = '--theme', 'dark',  '--script', 'page:demo-lighting' },
    @{ n = 'unsupported-light';     a = '--theme', 'light', '--script', 'page:demo-lighting' },
    @{ n = 'incompatible-dark';     a = '--theme', 'dark',  '--script', 'page:demo-future' },
    @{ n = 'textrow-expanded-dark';  a = '--theme', 'dark',  '--script', "$fan;page:demo-fan;expand-all;wait:600" },
    @{ n = 'textrow-expanded-light'; a = '--theme', 'light', '--script', "$fan;page:demo-fan;expand-all;wait:600" },
    @{ n = 'textrow-tall-dark';      a = '--theme', 'dark',  '--size', '1240x1000', '--script', "$fan;page:demo-fan;expand-all;scroll:300;wait:600" },
    @{ n = 'textrow-tall-light';     a = '--theme', 'light', '--size', '1240x1000', '--script', "$fan;page:demo-fan;expand-all;scroll:300;wait:600" },
    @{ n = 'textrow-narrow-dark';    a = '--theme', 'dark',  '--size', '700x900', '--script', "$fan;page:demo-fan;expand-all;wait:600" },
    @{ n = 'module-on-dark';        a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'module-on-light';       a = ('--theme', 'light', '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'module-off-dark';       a = '--theme', 'dark',  '--script', "$fan;disable:demo-fan;page:demo-fan" },
    @{ n = 'module-off-light';      a = '--theme', 'light', '--script', "$fan;disable:demo-fan;page:demo-fan" },
    @{ n = 'module-never-on-dark';  a = '--theme', 'dark',  '--script', 'page:demo-fan' },
    @{ n = 'approval-dark';         a = '--theme', 'dark',  '--script', "$fan;page:demo-fan;edit:row.mode.header=max;wait:900" },
    @{ n = 'approval-light';        a = '--theme', 'light', '--script', "$fan;page:demo-fan;edit:row.mode.header=max;wait:900" },
    @{ n = 'approval-system-dark';  a = '--theme', 'dark',  '--script', "$fan;page:demo-fan;edit:row.proxy.header=local;wait:900" },
    @{ n = 'approval-system-light'; a = '--theme', 'light', '--script', "$fan;page:demo-fan;edit:row.proxy.header=local;wait:900" },
    @{ n = 'approval-system-vi-dark'; a = '--theme', 'dark', '--lang', 'vi', '--script', "$fan;page:demo-fan;edit:row.proxy.header=local;wait:900" },
    @{ n = 'revert-system-dark';    a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.proxy.header=local;wait:700;approve:ask;revert:row.proxy.header;wait:900" },
    @{ n = 'revert-system-light';   a = '--theme', 'light', '--script', "$fan;approve:true;page:demo-fan;edit:row.proxy.header=local;wait:700;approve:ask;revert:row.proxy.header;wait:900" },
    @{ n = 'applied-dark';          a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;edit:row.pl1.header=60;wait:300" },
    @{ n = 'revert-dark';           a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-light';          a = '--theme', 'light', '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-vi-dark';        a = '--theme', 'dark',  '--lang', 'vi', '--script', "$fan;approve:true;page:demo-fan;edit:row.mode.header=max;wait:700;approve:ask;revert:row.mode.header;wait:900" },
    @{ n = 'revert-button-dark';    a = '--theme', 'dark',  '--script', "$fan;approve:true;page:demo-fan;edit:row.boost.header;wait:700;approve:ask;revert:row.boost.header;wait:900" },
    @{ n = 'unavailable-dark';      a = '--theme', 'dark',  '--size', '1240x1500', '--script', "$fan;page:demo-fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'unavailable-light';     a = '--theme', 'light', '--size', '1240x1500', '--script', "$fan;page:demo-fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-unavailable-dark';  a = '--theme', 'dark',  '--script', "$fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-unavailable-light'; a = '--theme', 'light', '--script', "$fan;cmd:demo-fan sensor off;wait:900" },
    @{ n = 'dashboard-vi-dark';     a = ('--theme', 'dark',  '--lang', 'vi') + $ts + ('--script', $visDash) },
    @{ n = 'module-vi-dark';        a = ('--theme', 'dark',  '--lang', 'vi', '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'module-vi-light';       a = ('--theme', 'light', '--lang', 'vi', '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'modules-dark';          a = '--theme', 'dark',  '--script', "$fan;page:modules" },
    @{ n = 'modules-light';         a = '--theme', 'light', '--script', "$fan;page:modules" },
    @{ n = 'settings-dark';         a = '--theme', 'dark',  '--size', '1240x1100', '--script', "$fan;page:settings" },
    @{ n = 'settings-light';        a = '--theme', 'light', '--size', '1240x1100', '--script', "$fan;page:settings" },
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
    @{ n = 'module-on-tall-dark';   a = ('--theme', 'dark',  '--size', '1240x1500') + $ts + ('--script', $visFan) },
    @{ n = 'narrow-modules-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:modules" },
    @{ n = 'narrow-settings-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:settings" },
    @{ n = 'narrow-log-dark'; a = '--theme', 'dark',  '--size', '640x700', '--script', "$fan;page:log" },
    # V1: the readings made visual. Tiles, sparklines and the trend chart of the Fan studio demo page (made-up numbers), dark and light, Vietnamese, narrow, scaled
    @{ n = 'visual-module-dark';    a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'visual-module-light';   a = ('--theme', 'light', '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'visual-module-vi-dark'; a = ('--theme', 'dark',  '--lang', 'vi', '--size', '1240x1040') + $ts + ('--script', $visFan) },
    @{ n = 'visual-hover-dark';     a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', "$visFan;hover:0.72") },
    @{ n = 'visual-hover-light';    a = ('--theme', 'light', '--size', '1240x1040') + $ts + ('--script', "$visFan;hover:0.72") },
    @{ n = 'visual-power-dark';     a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', "$visFan;chart:1;hover:0.55") },
    @{ n = 'visual-power-light';    a = ('--theme', 'light', '--size', '1240x1040') + $ts + ('--script', "$visFan;chart:1;hover:0.55") },
    @{ n = 'visual-range-1min-dark';a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', "$visFan;range:1") },
    @{ n = 'visual-narrow-dark';    a = ('--theme', 'dark',  '--size', '640x1500') + $ts + ('--script', $visFan) },
    @{ n = 'visual-narrow-light';   a = ('--theme', 'light', '--size', '640x1500') + $ts + ('--script', $visFan) },
    @{ n = 'visual-scale150-dark';  a = ('--theme', 'dark',  '--accent', '7A5AF8', '--shot-scale', '1.5') + $ts + ('--script', $visFan) },
    @{ n = 'visual-scale200-dark';  a = ('--theme', 'dark',  '--accent', '7A5AF8', '--shot-scale', '2') + $ts + ('--script', $visFan) },
    @{ n = 'visual-scale200-light'; a = ('--theme', 'light', '--accent', '7A5AF8', '--shot-scale', '2') + $ts + ('--script', $visFan) },
    @{ n = 'visual-dashboard-dark';  a = ('--theme', 'dark')  + $ts + ('--script', $visDash) },
    @{ n = 'visual-dashboard-light'; a = ('--theme', 'light') + $ts + ('--script', $visDash) },
    @{ n = 'visual-dashboard-narrow-dark'; a = ('--theme', 'dark', '--size', '640x700') + $ts + ('--script', $visDash) },
    @{ n = 'visual-unavailable-dark'; a = ('--theme', 'dark', '--size', '1240x1040') + $ts + ('--script', "$visFan;chart:2;cmd:demo-fan sensor off;wait:5000") },
    @{ n = 'visual-empty-dark';     a = ('--theme', 'dark',  '--size', '1240x1040') + $ts + ('--script', 'enable:demo-fan;page:demo-fan;wait:2500') },
    # frames of the live chart for the README animation (see the GIF step below)
    @{ n = 'visual-live';           a = ('--theme', 'dark',  '--size', '1000x860') + $ts + ('--script', "$visFan;frames:44x230") },
    # A11 brand: the title bar mark follows the Windows accent (--accent stands in for it); near a state colour it is drawn in the quiet form
    @{ n = 'mark-blue-dark';        a = '--theme', 'dark',  '--accent', '0078D4', '--size', '900x300', '--script', 'page:dashboard' },
    @{ n = 'mark-blue-light';       a = '--theme', 'light', '--accent', '0078D4', '--size', '900x300', '--script', 'page:dashboard' },
    @{ n = 'mark-violet-dark';      a = '--theme', 'dark',  '--accent', '7A5AF8', '--size', '900x300', '--script', 'page:dashboard' },
    @{ n = 'mark-green-dark';       a = '--theme', 'dark',  '--accent', '10893E', '--size', '900x300', '--script', 'page:dashboard' },
    @{ n = 'mark-red-light';        a = '--theme', 'light', '--accent', 'E81123', '--size', '900x300', '--script', 'page:dashboard' },
    @{ n = 'mark-amber-dark';       a = '--theme', 'dark',  '--accent', 'FFB900', '--size', '900x300', '--script', 'page:dashboard' },
    # the same window rendered as if the display scale were 150 % and 200 % (RenderTargetBitmap of the window content, not the screen)
    @{ n = 'scale150-dashboard-dark';  a = ('--theme', 'dark',  '--accent', '7A5AF8', '--shot-scale', '1.5') + $ts + ('--script', $visDash) },
    @{ n = 'scale200-dashboard-dark';  a = ('--theme', 'dark',  '--accent', '7A5AF8', '--shot-scale', '2') + $ts + ('--script', $visDash) },
    @{ n = 'scale200-dashboard-light'; a = ('--theme', 'light', '--accent', '7A5AF8', '--shot-scale', '2') + $ts + ('--script', $visDash) },
    @{ n = 'scale150-modules-light';   a = '--theme', 'light', '--accent', '7A5AF8', '--shot-scale', '1.5', '--script', "$fan;page:modules" },
    @{ n = 'scale200-module-dark';     a = ('--theme', 'dark',  '--accent', '7A5AF8', '--shot-scale', '2') + $ts + ('--script', $visFan) },
@{ n = 'narrow-dark';           a = ('--theme', 'dark',  '--size', '860x700') + $ts + ('--script', $visFan) }
)
$only = @($Names)
if ($only.Count -gt 0) { $jobs = $jobs | Where-Object { $only -contains $_.n } }
foreach ($j in $jobs) {
    $png = Join-Path $Out ($j.n + '.png')
    Remove-Item $png -ErrorAction SilentlyContinue
    $run = @{ Wait = $true }
    $extra = @('--shot', $png, '--exit-after', '200', '--allow-multi') + $j.a
    # a fresh data folder per shot, so enabled/disabled choices never leak between states
    $data = Join-Path $Demo ('data-shot-' + $j.n)
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    $mods = $null
    for ($i = 0; $i -lt $extra.Count; $i++) { if ($extra[$i] -eq '--modules') { $mods = $extra[$i + 1]; $extra = $extra[0..($i - 1)] + $(if ($i + 2 -lt $extra.Count) { $extra[($i + 2)..($extra.Count - 1)] } else { @() }); break } }
    if ($mods) { & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules $mods @extra | Out-Null }
    else { & "$PSScriptRoot\run-elevated.ps1" -Wait -Data $data -Modules $demoModules @extra | Out-Null }
    "{0}: {1}" -f $j.n, (Test-Path $png)
}

# The README animation: the frames the window wrote (PrintWindow of its own HWND, see the frames: script step) become one looping GIF with ffmpeg
# when ffmpeg is on the PATH (this script never installs it). Without ffmpeg the frames stay in tools\shots\visual-live-frames.
$frames = Join-Path $Out 'visual-live-frames'
if ((Test-Path $frames) -and (($only.Count -eq 0) -or ($only -contains 'visual-live'))) {
    $ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($ff) {
        $gif = Join-Path $Out 'visual-live.gif'
        & $ff.Source -y -v error -framerate 4.3 -i (Join-Path $frames 'f%03d.png') -vf 'scale=760:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4' -loop 0 $gif
        if (Test-Path $gif) { Remove-Item $frames -Recurse -Force; 'visual-live.gif: {0:N0} KB' -f ((Get-Item $gif).Length / 1KB) }
    }
    else { 'ffmpeg is not on the PATH: the frames are in ' + $frames + ' (put them together with ffmpeg: -framerate 4 -i f%03d.png visual-live.gif)' }
}
