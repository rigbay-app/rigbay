using System.Runtime.InteropServices;

namespace Rigbay.PowerRules.Windows;

/// <summary>
/// The real power source. Changes come from <c>RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)</c> on a message-only window of
/// its own thread (no polling: an idle module costs nothing); <see cref="Read"/> is one <c>GetSystemPowerStatus</c> call.
/// </summary>
internal sealed class WindowsPowerSource : IPowerSourcePort
{
    private const uint WmPowerBroadcast = 0x0218, WmClose = 0x0010, WmDestroy = 0x0002;
    private const int PbtApmResumeAutomatic = 0x0012, PbtApmResumeSuspend = 0x0007, PbtPowerSettingChange = 0x8013;
    private static readonly IntPtr HwndMessage = new(-3);

    // GUID_ACDC_POWER_SOURCE: the data of its notification is a DWORD, 0 AC, 1 DC (battery), 2 short-term (UPS).
    private static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    private static int _classCounter;

    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _proc;   // kept alive for the window's life
    private volatile bool _disposed;
    private volatile bool _listening;

    public bool Listening => _listening;

    public event Action? SourceChanged;
    public event Action? Resumed;

    // ------------------------------------------------------------------ reads

    public bool HasBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return false;
        // BatteryFlag: 128 = no system battery, 255 = unknown.
        return s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }

    public PowerSource Read()
    {
        if (!GetSystemPowerStatus(out var s)) return PowerSource.Unknown;
        return s.ACLineStatus switch { 1 => PowerSource.Plugged, 0 => PowerSource.Battery, _ => PowerSource.Unknown };
    }

    // ------------------------------------------------------------------ notifications

    public void Start()
    {
        if (_thread is not null || _disposed) return;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => Pump(ready)) { IsBackground = true, Name = "Rigbay power-rules source" };
        _thread = thread;
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Pump(ManualResetEventSlim ready)
    {
        string className = "RigbayPowerRules" + Interlocked.Increment(ref _classCounter) + "_" + Environment.ProcessId;
        IntPtr instance = GetModuleHandleW(null);
        IntPtr notification = IntPtr.Zero;
        bool registered = false;
        try
        {
            _proc = WindowProc;
            var wc = new WndClassEx { cbSize = (uint)Marshal.SizeOf<WndClassEx>(), lpfnWndProc = _proc, hInstance = instance, lpszClassName = className };
            if (RegisterClassExW(ref wc) == 0) return;
            registered = true;
            _hwnd = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            var guid = AcDcPowerSource;
            notification = RegisterPowerSettingNotification(_hwnd, ref guid, 0);   // DEVICE_NOTIFY_WINDOW_HANDLE
            _listening = notification != IntPtr.Zero;
            ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception) { /* a thread of ours: nothing may escape */ }
        finally
        {
            try
            {
                if (notification != IntPtr.Zero) UnregisterPowerSettingNotification(notification);
                if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
                if (registered) UnregisterClassW(className, instance);
            }
            catch (Exception) { }
            _hwnd = IntPtr.Zero;
            ready.Set();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == WmPowerBroadcast)
            {
                int kind = wParam.ToInt32();
                if (kind == PbtPowerSettingChange && lParam != IntPtr.Zero)
                {
                    var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == AcDcPowerSource) SourceChanged?.Invoke();
                }
                else if (kind is PbtApmResumeAutomatic or PbtApmResumeSuspend) Resumed?.Invoke();
                return new IntPtr(1);   // TRUE
            }
            if (message == WmClose) { DestroyWindow(hwnd); return IntPtr.Zero; }
            if (message == WmDestroy) { PostQuitMessage(0); return IntPtr.Zero; }
        }
        catch (Exception) { /* raised on the window thread, outside the host's fault boundary */ }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    // ------------------------------------------------------------------ native

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
