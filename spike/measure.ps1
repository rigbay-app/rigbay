# Cold-start numbers: runs the host N times and reads "first frame rendered" (ms since process creation) from its log.
# The first run after a build is reported separately (cold disk cache, first JIT of a fresh build).
param([ValidateSet('fd', 'sc')][string]$Variant = 'fd', [int]$Runs = 6)
$dir = Join-Path $PSScriptRoot "out\host-$Variant"
$size = (Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$ms = @()
for ($i = 1; $i -le $Runs; $i++) {
    $log = Join-Path $PSScriptRoot "out\measure-$Variant-$i.log"
    & "$PSScriptRoot\run-elevated.ps1" -Variant $Variant -Wait -HostArgs @('--log', $log, '--exit-after', '100', '--allow-multi') | Out-Null
    if (Test-Path $log) {
        $l = Select-String -Path $log -Pattern 'first frame rendered' | Select-Object -First 1
        if ($l -and $l.Line -match '\[\s*(\d+) ms\]') { $ms += [int]$Matches[1] }
    }
}
"{0}: {1:N1} MB; first frame ms per run: {2}" -f $Variant, $size, ($ms -join ', ')
if ($ms.Count -gt 1) { $w = $ms | Select-Object -Skip 1 | Sort-Object; "  warm median {0} ms (runs 2..{1})" -f $w[[int][Math]::Floor($w.Count / 2)], $Runs }
