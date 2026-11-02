# Checks the relative links of Markdown files: [text](path), [text](path#anchor), ![alt](image) and <img src="...">.
# External links (http, https, mailto) are not fetched; a link to a file that is not tracked or not on disk is reported.
# An #anchor is checked against the headings of the target Markdown file (GitHub's slug rules, simplified).
#   -Files   Markdown files to check (default: every tracked *.md except docs/plan and spike)
# Exit code 0 = no broken link, 1 = broken links.
param([string[]]$Files)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $root
try {
    if (-not $Files) {
        $Files = git ls-files '*.md' | Where-Object { $_ -notmatch '^(docs/plan|spike)/' -and $_ -notmatch '^\.claude/' }
    }
    function Get-Slugs([string]$path) {
        $slugs = New-Object System.Collections.Generic.HashSet[string]
        $inFence = $false
        foreach ($l in Get-Content -LiteralPath $path -Encoding utf8) {
            if ($l -match '^\s*```') { $inFence = -not $inFence; continue }
            if ($inFence) { continue }
            if ($l -match '^#{1,6}\s+(.*?)\s*#*\s*$') {
                $t = $Matches[1].ToLowerInvariant() -replace '[`*_]', '' -replace '\[([^\]]*)\]\([^)]*\)', '$1'
                $s = ($t -replace '[^\p{L}\p{N}\s-]', '') -replace '\s', '-'
                [void]$slugs.Add($s)
            }
        }
        return ,$slugs
    }
    $bad = New-Object System.Collections.Generic.List[string]
    $count = 0
    foreach ($f in $Files) {
        $dir = Split-Path (Join-Path $root $f) -Parent
        $inFence = $false
        $n = 0
        foreach ($line in Get-Content -LiteralPath (Join-Path $root $f) -Encoding utf8) {
            $n++
            if ($line -match '^\s*```') { $inFence = -not $inFence; continue }
            if ($inFence) { continue }
            $text = [regex]::Replace($line, '`[^`]*`', '')
            $targets = @()
            foreach ($m in [regex]::Matches($text, '\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')) { $targets += $m.Groups[1].Value }
            foreach ($m in [regex]::Matches($text, '<img[^>]+src="([^"]+)"')) { $targets += $m.Groups[1].Value }
            foreach ($t in $targets) {
                if ($t -match '^(https?:|mailto:|tel:)' ) { continue }
                $count++
                $path, $anchor = $t -split '#', 2
                $path = [uri]::UnescapeDataString($path)
                if ($path -eq '') { $full = Join-Path $root $f }
                elseif ($path.StartsWith('/')) { $full = Join-Path $root $path.TrimStart('/') }
                else { $full = Join-Path $dir $path }
                if (-not (Test-Path -LiteralPath $full)) { $bad.Add("${f}:${n}: missing target '$t'"); continue }
                if ($anchor -and (Test-Path -LiteralPath $full -PathType Leaf) -and $full -match '\.md$') {
                    if (-not (Get-Slugs $full).Contains($anchor.ToLowerInvariant())) { $bad.Add("${f}:${n}: no heading for anchor '#$anchor' in '$path'") }
                }
            }
        }
    }
    if ($bad.Count) { $bad | ForEach-Object { Write-Host "BROKEN $_" }; Write-Host "check-links: $($bad.Count) broken of $count relative links in $(@($Files).Count) files"; exit 1 }
    Write-Host "check-links: $count relative links in $(@($Files).Count) files, none broken"
}
finally { Pop-Location }
