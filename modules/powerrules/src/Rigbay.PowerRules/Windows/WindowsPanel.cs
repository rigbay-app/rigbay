using System.Runtime.InteropServices;

namespace Rigbay.PowerRules.Windows;

/// <summary>
/// The real internal panel. Finding it is read only (<c>QueryDisplayConfig</c>, <c>DisplayConfigGetDeviceInfo</c>, <c>EnumDisplaySettingsEx</c>).
/// The one write is <c>ChangeDisplaySettingsEx</c> with <c>DM_DISPLAYFREQUENCY</c>, flags 0 (this session only; <c>CDS_UPDATEREGISTRY</c> is not
/// used) after the same call with <c>CDS_TEST</c>. Both re-check, inside this class, that the display is the internal panel and that the
/// rate is one the panel lists at its current resolution, whatever the caller passes.
/// </summary>
internal sealed class WindowsPanel : IPanelPort
{
    // The only flags this class ever passes to ChangeDisplaySettingsEx: nothing (a change for this session) and CDS_TEST. Not CDS_UPDATEREGISTRY,
    // not CDS_GLOBAL, not CDS_FULLSCREEN, not CDS_RESET: a test scans the source for them.
    private const uint ChangeNow = 0, ChangeTest = 0x00000002;

    private const int EnumCurrentSettings = -1;
    private const uint DmPosition = 0x20, DmDisplayOrientation = 0x80, DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFlags = 0x200000, DmDisplayFrequency = 0x400000;
    private const uint DmInterlaced = 2;
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorInsufficientBuffer = 122;

    // Layout of the structures, by offset (the calls take byte buffers, so no structure can be declared wrong).
    private const int PathSize = 72, ModeSize = 64;
    private const int PathSourceAdapterLow = 0, PathSourceAdapterHigh = 4, PathSourceId = 8, PathTargetAdapterLow = 20, PathTargetAdapterHigh = 24, PathTargetId = 28, PathOutputTechnology = 36;
    private const uint SourceDeviceName = 1, TargetDeviceName = 2;

    // DEVMODEW, 220 bytes.
    private const int DevModeSize = 220;
    private const int DmSize = 68, DmFields = 72, DmPositionX = 76, DmPositionY = 80, DmOrientation = 84, DmBits = 168, DmWidth = 172, DmHeight = 176, DmFlags = 180, DmFrequency = 184;

    /// <summary>Output technologies that mean a laptop's own panel: internal, embedded DisplayPort, embedded UDI.</summary>
    public static bool IsInternal(uint outputTechnology) => outputTechnology is 0x80000000u or 11u or 13u;

    // ------------------------------------------------------------------ find

    public PanelLookup Find()
    {
        try
        {
            var found = FindDevice();
            if (found.State != PanelState.Active) return new PanelLookup(found.State);
            return ReadPanel(found.Device!, found.Name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return new PanelLookup(PanelState.Unreadable);
        }
    }

    private static (PanelState State, string? Device, string Name) FindDevice()
    {
        byte[]? paths = null, modes = null;
        uint pathCount = 0, modeCount = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount) != 0) return (PanelState.Unreadable, null, "");
            paths = new byte[Math.Max(1, (int)pathCount) * PathSize];
            modes = new byte[Math.Max(1, (int)modeCount) * ModeSize];
            int rc = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (rc == 0) break;
            if (rc != ErrorInsufficientBuffer || attempt == 2) return (PanelState.Unreadable, null, "");
            paths = null;
        }
        if (paths is null) return (PanelState.Unreadable, null, "");

        for (int i = 0; i < pathCount; i++)
        {
            int at = i * PathSize;
            uint technology = BitConverter.ToUInt32(paths, at + PathOutputTechnology);
            if (!IsInternal(technology)) continue;
            uint sourceLow = BitConverter.ToUInt32(paths, at + PathSourceAdapterLow);
            int sourceHigh = BitConverter.ToInt32(paths, at + PathSourceAdapterHigh);
            uint sourceId = BitConverter.ToUInt32(paths, at + PathSourceId);
            uint targetLow = BitConverter.ToUInt32(paths, at + PathTargetAdapterLow);
            int targetHigh = BitConverter.ToInt32(paths, at + PathTargetAdapterHigh);
            uint targetId = BitConverter.ToUInt32(paths, at + PathTargetId);

            var device = RequestName(SourceDeviceName, sourceLow, sourceHigh, sourceId, 84, 20, 32);
            if (string.IsNullOrEmpty(device)) return (PanelState.Unreadable, null, "");
            var friendly = RequestName(TargetDeviceName, targetLow, targetHigh, targetId, 420, 36, 64) ?? "";
            return (PanelState.Active, device, friendly);
        }
        return (PanelState.NotActive, null, "");
    }

    /// <summary>One <c>DisplayConfigGetDeviceInfo</c> request for a name: the header (type, size, adapter, id), then a UTF-16 string at <paramref name="nameAt"/>.</summary>
    private static string? RequestName(uint type, uint adapterLow, int adapterHigh, uint id, int size, int nameAt, int nameChars)
    {
        var buffer = new byte[size];
        BitConverter.GetBytes(type).CopyTo(buffer, 0);
        BitConverter.GetBytes((uint)size).CopyTo(buffer, 4);
        BitConverter.GetBytes(adapterLow).CopyTo(buffer, 8);
        BitConverter.GetBytes(adapterHigh).CopyTo(buffer, 12);
        BitConverter.GetBytes(id).CopyTo(buffer, 16);
        if (DisplayConfigGetDeviceInfo(buffer) != 0) return null;
        // The source request has its name right after the 20-byte header; the target request has 16 bytes of fields (flags, output
        // technology, EDID ids, connector instance) first, so its friendly name starts at 36.
        var text = System.Text.Encoding.Unicode.GetString(buffer, nameAt, nameChars * 2);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static PanelLookup ReadPanel(string device, string name)
    {
        var current = Alloc();
        try
        {
            if (!EnumDisplaySettingsExW(device, EnumCurrentSettings, current, 0)) return new PanelLookup(PanelState.Unreadable);
            int width = ReadInt(current, DmWidth), height = ReadInt(current, DmHeight), bits = ReadInt(current, DmBits), hz = ReadInt(current, DmFrequency);
            int orientation = ReadInt(current, DmOrientation);
            if (width <= 0 || height <= 0 || hz <= 1) return new PanelLookup(PanelState.Unreadable);

            var rates = new SortedSet<int>();
            var scratch = Alloc();
            try
            {
                for (int i = 0; i < 4096; i++)
                {
                    if (!EnumDisplaySettingsExW(device, i, scratch, 0)) break;
                    if (ReadInt(scratch, DmWidth) != width || ReadInt(scratch, DmHeight) != height || ReadInt(scratch, DmBits) != bits) continue;
                    if (ReadInt(scratch, DmOrientation) != orientation) continue;
                    if ((ReadInt(scratch, DmFlags) & DmInterlaced) != 0) continue;
                    int rate = ReadInt(scratch, DmFrequency);
                    if (rate > 1) rates.Add(rate);   // 0 and 1 mean "the hardware default", not a rate
                }
            }
            finally { Marshal.FreeHGlobal(scratch); }
            rates.Add(hz);
            return new PanelLookup(PanelState.Active, new PanelInfo(device, name, width, height, hz, rates.ToArray()));
        }
        finally { Marshal.FreeHGlobal(current); }
    }

    // ------------------------------------------------------------------ change

    public DisplayChange Test(PanelInfo panel, int hz) => Change(panel, hz, ChangeTest);

    public DisplayChange Apply(PanelInfo panel, int hz) => Change(panel, hz, ChangeNow);

    private static DisplayChange Change(PanelInfo panel, int hz, uint flags)
    {
        // Re-check here, from Windows, not from what the caller says: this is the internal panel, and Windows lists this rate for it now.
        var found = FindDevice();
        if (found.State != PanelState.Active || !string.Equals(found.Device, panel.DeviceName, StringComparison.OrdinalIgnoreCase)) return new DisplayChange(-5);   // DISP_CHANGE_BADPARAM
        var fresh = ReadPanel(found.Device!, found.Name);
        if (fresh.Panel is null || !fresh.Panel.Lists(hz) || hz <= 1) return new DisplayChange(-2);   // DISP_CHANGE_BADMODE

        var mode = Alloc();
        try
        {
            if (!EnumDisplaySettingsExW(panel.DeviceName, EnumCurrentSettings, mode, 0)) return new DisplayChange(-1);
            // Everything but the rate is what the display has now, so the call moves nothing else.
            WriteInt(mode, DmFrequency, hz);
            WriteUInt(mode, DmFields, DmPosition | DmDisplayOrientation | DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFlags | DmDisplayFrequency);
            return new DisplayChange(ChangeDisplaySettingsExW(panel.DeviceName, mode, IntPtr.Zero, flags, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(mode); }
    }

    // ------------------------------------------------------------------ DEVMODE as bytes

    private static IntPtr Alloc()
    {
        var p = Marshal.AllocHGlobal(DevModeSize);
        for (int i = 0; i < DevModeSize; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt16(p, DmSize, DevModeSize);
        return p;
    }

    private static int ReadInt(IntPtr p, int offset) => Marshal.ReadInt32(p, offset);
    private static void WriteInt(IntPtr p, int offset, int value) => Marshal.WriteInt32(p, offset, value);
    private static void WriteUInt(IntPtr p, int offset, uint value) => Marshal.WriteInt32(p, offset, unchecked((int)value));

    // ------------------------------------------------------------------ native

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] byte[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string deviceName, int modeNumber, IntPtr devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);
}
