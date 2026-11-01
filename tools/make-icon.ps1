# Draws the Rigbay mark (three bays, the last one filled) and writes src\Rigbay.App\Assets\rigbay.ico
# with PNG frames at 16, 24, 32, 48, 64 and 256 px. Run once; the .ico is committed.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\Rigbay.App\Assets\rigbay.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $size / 32.0
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 150, 160, 178)), ([Math]::Max(1.0, 2.0 * $u))
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 47, 182, 124))
    $w = 6.0 * $u; $h = 24.0 * $u; $top = 4.0 * $u; $r = 2.0 * $u
    foreach ($i in 0..2) {
        $x = (4.0 + $i * 9.0) * $u + $u
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $path.AddArc($x, $top, $d, $d, 180, 90)
        $path.AddArc($x + $w - $d, $top, $d, $d, 270, 90)
        $path.AddArc($x + $w - $d, $top + $h - $d, $d, $d, 0, 90)
        $path.AddArc($x, $top + $h - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        if ($i -eq 2) { $g.FillPath($fill, $path) } else { $g.DrawPath($outline, $path) }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$frames = $sizes | ForEach-Object { , (Frame $_) }
$stream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $stream
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $png = $frames[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$png.Length); $bw.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $frames) { $bw.Write($png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $stream.ToArray())
"wrote $out ($((Get-Item $out).Length) bytes)"
