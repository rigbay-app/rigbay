# Helpers for the E2E driver (tools\e2e\flow.ps1): out-of-process UI Automation against the REAL Rigbay window, a PrintWindow of the
# app's own HWND (never the screen), process helpers. Dot-source it from an ELEVATED Windows PowerShell 5.1 (the app is elevated; a normal
# process cannot drive an elevated window). Nothing here writes outside the folders the caller passes.
Set-StrictMode -Version 2
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class E2eWin
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <summary>PrintWindow (PW_RENDERFULLCONTENT) of this one window into a PNG. Returns "WxH" or an error text.</summary>
    public static string Shot(IntPtr hwnd, string png)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        RECT r; if (!GetWindowRect(hwnd, out r)) return "error: GetWindowRect";
        int w = r.R - r.L, h = r.B - r.T;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dc = g.GetHdc();
                bool ok = PrintWindow(hwnd, dc, 2);
                g.ReleaseHdc(dc);
                if (!ok) return "error: PrintWindow";
            }
            bmp.Save(png, ImageFormat.Png);
        }
        return w + "x" + h;
    }

    public static bool Resize(IntPtr hwnd, int x, int y, int w, int h)
    {
        // SWP_NOZORDER | SWP_NOACTIVATE
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010);
    }
}
'@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:CT = [System.Windows.Automation.ControlType]
$script:ReportFile = $null

function Say([string]$s) {
    $line = '{0:HH:mm:ss.fff} {1}' -f (Get-Date), $s
    Write-Host $line
    if ($script:ReportFile) { Add-Content -LiteralPath $script:ReportFile -Value $line -Encoding UTF8 }
}

# ---------------------------------------------------------------------------------------------------- processes and windows

function Start-App([string]$Exe, [string[]]$AppArgs) {
    $argLine = ($AppArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath $Exe -ArgumentList $argLine -WorkingDirectory (Split-Path $Exe) -PassThru
    Say "started $Exe $argLine -> pid $($p.Id)"
    $p
}

function Get-AppWindow([int]$ProcId, [int]$TimeoutSec = 40) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $ProcId
        $w = $script:AE::RootElement.FindFirst($script:TS::Children, $cond)
        if ($w -and $w.Current.NativeWindowHandle -ne 0 -and $w.Current.Name -eq 'Rigbay') { return $w }
        Start-Sleep -Milliseconds 300
    }
    $null
}

# Other workers run Rigbay.App on this machine too (demo modules, self-tests). Everything here selects ONLY the processes whose command
# line names this run's scratch data folder ("--data <base>\data"), so a foreign process is never driven, waited for or stopped.
$script:Base = $null
function Get-MyApps {
    $needle = ('--data ' + $script:Base + '\').ToLowerInvariant()
    $needleQ = ('--data "' + $script:Base + '\').ToLowerInvariant()
    @(Get-CimInstance Win32_Process -Filter "Name='Rigbay.App.exe'" | Where-Object {
            $c = ([string]$_.CommandLine).ToLowerInvariant(); $c.Contains($needle) -or $c.Contains($needleQ)
        } | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_ })
}

function Get-ForeignApps { @(Get-Process Rigbay.App -ErrorAction SilentlyContinue | Where-Object { (Get-MyApps | ForEach-Object Id) -notcontains $_.Id }) }

function Wait-NewApp([int[]]$ExceptPids, [int]$TimeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-MyApps | Where-Object { $ExceptPids -notcontains $_.Id } | Select-Object -First 1
        if ($p) { return $p }
        Start-Sleep -Milliseconds 300
    }
    $null
}

function Stop-MyApps {
    Get-MyApps | ForEach-Object { Say "stopping Rigbay.App pid $($_.Id) (this run's scratch data folder)"; try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { } }
}

# ---------------------------------------------------------------------------------------------------- UIA

function Find-El($Root, [string]$Name, $Type = $null, [int]$TimeoutSec = 10, [switch]$Contains, [switch]$Quiet) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        if ($Contains) {
            foreach ($e in $Root.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                try { if ($e.Current.Name -like "*$Name*" -and ($null -eq $Type -or $e.Current.ControlType.Id -eq $Type.Id)) { return $e } } catch { }
            }
        }
        else {
            $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name
            if ($Type) { $c = New-Object System.Windows.Automation.AndCondition $c, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), $Type) }
            $e = $Root.FindFirst($script:TS::Descendants, $c)
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (-not $Quiet) { Say "  (not found: '$Name')" }
    $null
}

function Test-El($Root, [string]$Name, $Type = $null, [switch]$Contains) { [bool](Find-El $Root $Name $Type -TimeoutSec 0 -Quiet -Contains:$Contains) }

function Invoke-El($El) {
    try { $p = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $p.Invoke(); return $true } catch { }
    try { $p = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern); $p.Select(); return $true } catch { }
    try { $p = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $p.Toggle(); return $true } catch { }
    $false
}

function Click-Button($Win, [string]$Name, [int]$TimeoutSec = 10) {
    $e = Find-El $Win $Name $script:CT::Button $TimeoutSec
    if (-not $e) { return $false }
    if (-not $e.Current.IsEnabled) { Say "  button '$Name' is disabled"; return $false }
    $ok = Invoke-El $e
    Say "  pressed '$Name': $ok"
    $ok
}

function Set-Toggle($El, [bool]$On) {
    $p = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $now = $p.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($now -ne $On) { $p.Toggle() }
}

function Get-Toggle($El) { $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On }

function Wait-Until([scriptblock]$Cond, [int]$TimeoutSec = 30, [int]$PollMs = 250) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do { try { if (& $Cond) { return $true } } catch { }; Start-Sleep -Milliseconds $PollMs } while ((Get-Date) -lt $deadline)
    $false
}

function Get-Texts($Root) {
    # every non-empty Name in the window, in tree order, with its control type: the page's visible words
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($e in $Root.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try { $n = $e.Current.Name; if ($n -and $n.Trim().Length -gt 0) { $out.Add(('[{0}] {1}' -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), ($n -replace '\s+', ' '))) } } catch { }
    }
    $out
}

function Dump-Tree($Root, [string]$File) { Get-Texts $Root | Set-Content -LiteralPath $File -Encoding UTF8 }

# ---------------------------------------------------------------------------------------------------- shots

function Take-Shot($Win, [string]$Png) {
    $h = [IntPtr]$Win.Current.NativeWindowHandle
    $r = [E2eWin]::Shot($h, $Png)
    Say "  shot $(Split-Path $Png -Leaf): $r"
    $r
}

function Resize-Win($Win, [int]$W, [int]$H) {
    $h = [IntPtr]$Win.Current.NativeWindowHandle
    [void][E2eWin]::Resize($h, 40, 20, $W, $H)
    Start-Sleep -Milliseconds 700
}

# The Installed rows of the Modules page are SettingsExpanders whose header is a stack of icon and text, so the expander button has no
# accessible name (finding E2E-03). Found by walking up from the row's "<Name>  v<version>" text to the ancestor that can expand.
function Open-InstalledRow($Win, [string]$DisplayName) {
    $t = Find-El $Win ($DisplayName + '  v') $script:CT::Text -Contains -TimeoutSec 5
    if (-not $t) { return $false }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $a = $t
    for ($i = 0; $i -lt 6 -and $a; $i++) {
        $a = $walker.GetParent($a)
        if ($a -and ($a.GetSupportedPatterns() | Where-Object { $_.Id -eq [System.Windows.Automation.ExpandCollapsePattern]::Pattern.Id })) {
            $a.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            Start-Sleep -Milliseconds 500
            return $true
        }
    }
    $false
}
