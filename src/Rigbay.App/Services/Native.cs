using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Rigbay.App.Services;

/// <summary>P/Invoke for what WinUI 3 does not have: window subclass, hotkeys, tray icon and menu, console attach, window-only capture.</summary>
static class Native
{
    public const int WM_HOTKEY = 0x0312, WM_APP_TRAY = 0x8001, WM_NULL = 0, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_SETTINGCHANGE = 0x001A, WM_THEMECHANGED = 0x031A, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, SPI_SETHIGHCONTRAST = 0x0043;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("shell32.dll")] public static extern bool IsUserAnAdmin();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint idOrSub, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern nint SendMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint hdc, uint flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int Size, W, H; public short Planes, Bits; public int Compression, SizeImage, XPpm, YPpm, ClrUsed, ClrImportant; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nint extra; }

    // keeps the delegate alive for the lifetime of the window
    static SubclassProc? _keep;

    public static void Subclass(nint hwnd, Func<uint, nint, nint, nint, (bool Handled, nint Result)> handler)
    {
        _keep = (h, m, w, l, id, d) =>
        {
            var (handled, result) = handler(m, w, l, h);
            return handled ? result : DefSubclassProc(h, m, w, l);
        };
        SetWindowSubclass(hwnd, _keep, 1, 0);
    }

    /// <summary>Let Win32 popup menus follow the system dark/light setting (undocumented uxtheme ordinals; harmless if they fail).</summary>
    public static void AllowDarkMenus()
    {
        try { SetPreferredAppMode(1 /* AllowDark */); FlushMenuThemes(); } catch { }
    }

    // ---------- tray ----------

    /// <summary>Pixels of a notification-area icon on the primary monitor (where the taskbar is): 16, 20, 24 or 32 at 100, 125, 150 and 200 %.</summary>
    public static int TrayIconSize()
    {
        try
        {
            var monitor = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
                return GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpiX);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return GetSystemMetrics(49 /* SM_CXSMICON */);
    }

    public static nint LoadTrayIcon(string icoPath, int size) =>
        LoadImage(0, icoPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);

    public static bool TrayAdd(nint hwnd, nint icon, string tip)
    {
        var d = NewIcon(hwnd, tip);
        d.uFlags = 0x7;                      // NIF_MESSAGE | NIF_ICON | NIF_TIP
        d.uCallbackMessage = WM_APP_TRAY;
        d.hIcon = icon;
        return Shell_NotifyIconW(0, ref d);  // NIM_ADD
    }

    public static bool TrayModify(nint hwnd, nint icon, string tip)
    {
        var d = NewIcon(hwnd, tip);
        d.uFlags = 0x7; d.uCallbackMessage = WM_APP_TRAY; d.hIcon = icon;
        return Shell_NotifyIconW(1, ref d);  // NIM_MODIFY
    }

    /// <summary>
    /// Shows a balloon (a toast banner on Windows 10 and 11) from the tray icon: NIM_MODIFY with NIF_INFO. Works for an elevated
    /// process, which <c>AppNotificationManager</c> does not. <paramref name="infoFlags"/>: 1 info, 2 warning, 3 error (NIIF_*).
    /// Windows sends NIN_BALLOONSHOW to the icon's callback when it actually displays it.
    /// </summary>
    public static bool TrayBalloon(nint hwnd, string title, string body, uint infoFlags)
    {
        var d = NewIcon(hwnd, "");
        d.uFlags = 0x10;                     // NIF_INFO only: the icon and tip stay as they are
        d.szInfoTitle = title.Length > 63 ? title[..62] + "…" : title;
        d.szInfo = body.Length > 255 ? body[..254] + "…" : body;
        d.dwInfoFlags = infoFlags;
        return Shell_NotifyIconW(1, ref d);  // NIM_MODIFY
    }

    public const int NIN_BALLOONSHOW = 0x402, NIN_BALLOONHIDE = 0x403, NIN_BALLOONTIMEOUT = 0x404, NIN_BALLOONUSERCLICK = 0x405;

    public static bool TrayRemove(nint hwnd) { var d = NewIcon(hwnd, ""); return Shell_NotifyIconW(2, ref d); }

    static NOTIFYICONDATA NewIcon(nint hwnd, string tip) =>
        new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, szTip = tip.Length > 127 ? tip[..127] : tip, szInfo = "", szInfoTitle = "" };

    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;

    /// <summary>Press and release Ctrl+Alt+F24 with SendInput; the host injects its own keys so no other program is touched.</summary>
    public static uint SendChord(ushort[] downs)
    {
        var seq = new List<INPUT>();
        foreach (var vk in downs) seq.Add(new INPUT { type = 1, ki = new KEYBDINPUT { vk = vk, flags = 0 } });
        foreach (var vk in downs.Reverse()) seq.Add(new INPUT { type = 1, ki = new KEYBDINPUT { vk = vk, flags = 2 } });
        return SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>());
    }

    // ---------- capture of the window itself (never the screen) ----------

    public static bool CaptureWindow(nint hwnd, string pngPath, out string info)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.R - r.L, h = r.B - r.T;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        bool ok = PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */);
        var bi = new BITMAPINFO { Size = 40, W = w, H = -h, Planes = 1, Bits = 32 };
        var px = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, px, ref bi, 0);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(0, screen);
        for (int i = 0; i < px.Length; i += 4) { (px[i], px[i + 2]) = (px[i + 2], px[i]); px[i + 3] = 255; }
        WritePng(pngPath, px, w, h);
        info = $"{w}x{h} dpi={GetDpiForWindow(hwnd)} printWindow={ok}";
        return ok;
    }

    public static void WritePng(string path, byte[] rgba, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(rgba, y * w * 4, w * 4); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) raw.WriteTo(zs);
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var t = Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t.Concat(data)) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        var crc = new byte[4]; BE(crc, 0, (int)~c); s.Write(crc);
    }
}
