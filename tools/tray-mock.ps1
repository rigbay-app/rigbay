# Draws a mock of the notification area (not the real taskbar, not the real menu): the app's tray icons by taskbar theme at the four
# frame sizes (100, 125, 150, 200 %), the neutral and high-contrast ones, and the menu the tray builds. Output: tools\shots\tray-mock.png
# The frames are the PNG exports in assets\brand: the same pixels as the frames inside the .ico files the app ships (System.Drawing cannot
# read PNG-entry .ico files; the app's self-test loads the real .ico files at all four sizes through LoadImage).
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot) 'tools\shots\tray-mock.png'))
Add-Type -AssemblyName System.Drawing
$brand = Join-Path (Split-Path $PSScriptRoot) 'assets\brand\app-icon'
$W = 760; $H = 440
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 128, 128, 128))
$font = New-Object System.Drawing.Font 'Segoe UI', 9
function Strip($y, $bg, $fg, $name, $label) {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $bg), 0, $y, $W, 56)
    $text = New-Object System.Drawing.SolidBrush $fg
    $g.DrawString($label, $font, $text, 10, $y + 20)
    $folder = if ($name -like 'rigbay-hc*') { 'high-contrast\png' } else { 'tray\png' }
    $x = 250
    foreach ($s in 16, 20, 24, 32) {
        $img = [System.Drawing.Image]::FromFile((Join-Path $brand "$folder\$name-$s.png"))
        $g.DrawImage($img, $x, ($y + (56 - $s) / 2), $s, $s)
        $img.Dispose()
        $g.DrawString("$s px", $font, $text, $x + $s + 4, $y + 20)
        $x += $s + 60
    }
}
Strip 10  ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))    ([System.Drawing.Color]::White) 'rigbay-tray-dark'    'dark taskbar: rigbay-tray-dark'
Strip 70  ([System.Drawing.Color]::FromArgb(255, 243, 243, 243)) ([System.Drawing.Color]::Black) 'rigbay-tray-light'   'light taskbar: rigbay-tray-light'
Strip 130 ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))    ([System.Drawing.Color]::White) 'rigbay-tray-neutral' 'theme unreadable: neutral'
Strip 190 ([System.Drawing.Color]::Black)                        ([System.Drawing.Color]::White) 'rigbay-hc-white'     'high contrast (dark): hc-white'
Strip 250 ([System.Drawing.Color]::White)                        ([System.Drawing.Color]::Black) 'rigbay-hc-black'     'high contrast (light): hc-black'
# the menu as TrayHost.BuildModel makes it (self-test: Open, one submenu per module with tray items, Quit)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 44, 44, 44))), 520, 316, 200, 112)
$y = 322
foreach ($t in 'Open Rigbay', '-', 'Fan studio (demo)    >', '-', 'Quit') {
    if ($t -eq '-') { $g.DrawLine((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 90, 90, 90))), 530, $y + 4, 710, $y + 4); $y += 9 }
    else { $g.DrawString($t, $font, [System.Drawing.Brushes]::White, 534, $y); $y += 24 }
}
$g.DrawString('menu model (dark)', $font, [System.Drawing.Brushes]::White, 10, 330)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"wrote $Out"
