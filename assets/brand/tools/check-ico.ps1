# Loads every .ico of the kit the way Windows does: LoadImage(LR_LOADFROMFILE) at each size (the call TrayHost/Native.LoadTrayIcon makes)
# and System.Drawing.Icon. Reports the size Windows actually returned. No window is shown, nothing is captured from the screen.
# Run: powershell -NoProfile -File assets/brand/tools/check-ico.ps1
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Rb -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr LoadImage(System.IntPtr hinst, string name, uint type, int cx, int cy, uint load);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(System.IntPtr h);
'@
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$fails = 0
foreach ($f in Get-ChildItem -Path $root -Recurse -Filter *.ico | Where-Object { $_.FullName -notmatch '\\integration\\' }) {
    $rel = $f.FullName.Substring($root.Path.Length + 1)
    $line = "{0,-46}" -f $rel
    foreach ($s in 16, 20, 24, 32, 48, 64, 256) {
        $h = [Rb.Native]::LoadImage([IntPtr]::Zero, $f.FullName, 1, $s, $s, 0x10)
        if ($h -eq [IntPtr]::Zero) { $line += " $s=none"; continue }
        $ic = [System.Drawing.Icon]::FromHandle($h)
        $line += " $s=$($ic.Width)x$($ic.Height)"
        if ($ic.Width -ne $s -and $s -in 16, 20, 24, 32) { $fails++ }
        [Rb.Native]::DestroyIcon($h) | Out-Null
    }
    $line
}
$whole = New-Object System.Drawing.Icon (Join-Path $root 'app-icon\rigbay.ico')
"System.Drawing.Icon(rigbay.ico) default size: $($whole.Width)x$($whole.Height)"
if ($fails) { "FAIL: $fails sizes did not load at the requested size"; exit 1 } else { 'ok: every tray size loaded at the requested size' }
