# Renders the host window itself (PrintWindow on its own HWND, never the screen) into docs\spike\*.png.
param([ValidateSet('fd', 'sc')][string]$Variant = 'fd')
$ErrorActionPreference = 'Stop'
$docs = Join-Path $PSScriptRoot '..\docs\spike'
New-Item -ItemType Directory -Force $docs | Out-Null
$empty = Join-Path $PSScriptRoot 'out\empty-modules'
New-Item -ItemType Directory -Force $empty | Out-Null
$jobs = @(
    @{ n = 'dashboard-dark';  a = '--theme', 'dark' },
    @{ n = 'dashboard-light'; a = '--theme', 'light' },
    @{ n = 'rows-dark';       a = '--theme', 'dark',  '--page', 'spike.rows' },
    @{ n = 'rows-light';      a = '--theme', 'light', '--page', 'spike.rows' },
    @{ n = 'failed-dark';     a = '--theme', 'dark',  '--page', 'spike.broken' },
    @{ n = 'empty-light';     a = '--theme', 'light', '--modules', $empty },
    @{ n = 'dashboard-vi';    a = '--theme', 'dark',  '--lang', 'vi' }
)
foreach ($j in $jobs) {
    $png = Join-Path $docs ($j.n + '.png')
    & "$PSScriptRoot\run-elevated.ps1" -Variant $Variant -Wait -HostArgs (@('--shot', $png, '--exit-after', '200', '--allow-multi') + $j.a) | Out-Null
    "{0}: {1}" -f $j.n, (Test-Path $png)
}
