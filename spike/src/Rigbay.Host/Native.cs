using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Rigbay.Host;

/// <summary>P/Invoke for the extras WinUI 3 does not have: tray icon, global hotkey, window subclass, window-only capture.</summary>
static class Native
{
    public const int WM_HOTKEY = 0x0312, WM_APP_TRAY = 0x8001;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    public const uint VK_F24 = 0x87;

    public delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] public static extern nint LoadIcon(nint hInst, nint name);
    [DllImport("shell32.dll")] public static extern bool IsUserAnAdmin();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint hdc, uint flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);

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

    static SubclassProc? _keep;   // delegate must outlive the window

    public static void Subclass(nint hwnd, Func<uint, nint, nint, bool> handler)
    {
        _keep = (h, m, w, l, id, d) => handler(m, w, l) ? 0 : DefSubclassProc(h, m, w, l);
        SetWindowSubclass(hwnd, _keep, 1, 0);
    }

    public static bool TrayAdd(nint hwnd, string tip)
    {
        var d = NewIcon(hwnd, tip);
        d.uFlags = 0x7;                      // NIF_MESSAGE | NIF_ICON | NIF_TIP
        d.uCallbackMessage = WM_APP_TRAY;
        d.hIcon = LoadIcon(0, 32512);        // IDI_APPLICATION
        return Shell_NotifyIconW(0, ref d);  // NIM_ADD
    }
    public static bool TrayRemove(nint hwnd) { var d = NewIcon(hwnd, ""); return Shell_NotifyIconW(2, ref d); }
    static NOTIFYICONDATA NewIcon(nint hwnd, string tip) =>
        new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, szTip = tip, szInfo = "", szInfoTitle = "" };

    /// <summary>Press and release Ctrl+Alt+F24 with SendInput. The host injects its own keys so no other app is touched.</summary>
    public static uint SendHotkeyChord()
    {
        INPUT K(ushort vk, bool up) => new() { type = 1, ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } };
        var seq = new[] { K(0x11, false), K(0x12, false), K((ushort)VK_F24, false), K((ushort)VK_F24, true), K(0x12, true), K(0x11, true) };
        return SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Renders only this window (PW_RENDERFULLCONTENT) into a PNG. Never reads the screen.</summary>
    public static bool CaptureWindow(nint hwnd, string pngPath, out string info)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.R - r.L, h = r.B - r.T;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        bool ok = PrintWindow(hwnd, mem, 2);
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
