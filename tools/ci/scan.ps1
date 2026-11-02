# Repository hygiene scan. Looks at tracked files only (git ls-files), so build output and local scratch are not scanned.
#   1. no tracked .dll .exe .pdb .nupkg .zip (binaries come from releases, not from git)
#   2. no strings that look like secrets (private keys, GitHub / cloud / Slack tokens, bearer headers, key = long value)
#   3. -Personal: no machine-specific strings (the local user profile name, the machine name). The needles are taken from the
#      environment of the machine that runs the script, not written in this file, so the file itself stays clean. The match is case-sensitive: the
#      public GitHub handle in the licence and in links is lower case and is not a finding. CI does not
#      pass -Personal (the runner has other names), so this part is a local check only; see docs/CI.md.
# Exit code 0 = clean, 1 = findings.
param([switch]$Personal)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    $files = git ls-files -z | ForEach-Object { $_ -split "`0" } | Where-Object { $_ }
    $findings = New-Object System.Collections.Generic.List[string]

    foreach ($f in $files) { if ($f -match '\.(dll|exe|pdb|nupkg|zip)$') { $findings.Add("binary tracked: $f") } }

    $patterns = [ordered]@{
        'private key block'     = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
        'GitHub token'          = '\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b'
        'AWS access key id'     = '\b(AKIA|ASIA)[0-9A-Z]{16}\b'
        'Slack token'           = '\bxox[abprs]-[A-Za-z0-9-]{10,}\b'
        'Google API key'        = '\bAIza[0-9A-Za-z_-]{35}\b'
        'OpenAI/Anthropic key'  = '\bsk-(ant-)?[A-Za-z0-9_-]{32,}\b'
        'bearer header'         = '(?i)authorization:\s*bearer\s+[A-Za-z0-9._~+/=-]{20,}'
        'secret assignment'     = '(?i)\b(api[_-]?key|secret|password|passwd|token)\b\s*[:=]\s*["'']?[A-Za-z0-9/+_=-]{24,}["'']?\s*$'
    }
    $binaryExt = '\.(png|ico|jpg|jpeg|gif|woff2?|ttf|otf|pdf)$'
    $needles = @()
    if ($Personal) {
        $needles = @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
        if (-not $needles) { Write-Host 'scan: -Personal has nothing to look for on this machine' }
    }
    foreach ($f in $files) {
        if ($f -match $binaryExt -or $f -match '\.(dll|exe|pdb|nupkg|zip)$') { continue }
        if ($f -eq 'tools/ci/scan.ps1') { continue }   # holds the patterns above
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadLines((Join-Path $root $f))) {
            $lineNo++
            foreach ($k in $patterns.Keys) {
                if ($line -cmatch $patterns[$k]) { $findings.Add("${k}: ${f}:$lineNo") }
            }
            foreach ($n in $needles) {
                if ($line.IndexOf($n, [StringComparison]::Ordinal) -ge 0) { $findings.Add("machine-specific string: ${f}:$lineNo") }
            }
        }
    }
    if ($findings.Count) { $findings | ForEach-Object { Write-Host "FOUND  $_" }; Write-Host "scan: $($findings.Count) finding(s)"; exit 1 }
    Write-Host "scan: clean ($($files.Count) tracked files$(if ($Personal) { ', personal strings included' }))"
}
finally { Pop-Location }
