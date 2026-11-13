using System.Runtime.InteropServices;

namespace Rigbay.PowerRules.Windows;

/// <summary>
/// The Windows power mode through the two undocumented exports of <c>powrprof.dll</c> that community tools use
/// (<c>PowerGetEffectiveOverlayScheme</c>, <c>PowerSetActiveOverlayScheme</c>). They are bound by name when first needed, so a Windows
/// without them gives <see cref="Available"/> false and "unknown" instead of an exception. The caller names a <see cref="PowerMode"/>; the
/// GUID comes from <see cref="PowerModes.Guids"/> and nothing else is ever passed.
/// </summary>
internal sealed class WindowsPowerMode : IPowerModePort
{
    private delegate uint GetEffective(out Guid scheme);
    private delegate uint SetActive(ref Guid scheme);

    private readonly object _gate = new();
    private bool _bound;
    private GetEffective? _get;
    private SetActive? _set;

    public bool Available
    {
        get
        {
            Bind();
            return _get is not null && _set is not null;
        }
    }

    private void Bind()
    {
        lock (_gate)
        {
            if (_bound) return;
            _bound = true;
            try
            {
                // By full path from System32: no search path.
                var path = Path.Combine(Environment.SystemDirectory, "powrprof.dll");
                if (!NativeLibrary.TryLoad(path, out var lib)) return;
                if (NativeLibrary.TryGetExport(lib, "PowerGetEffectiveOverlayScheme", out var get))
                    _get = Marshal.GetDelegateForFunctionPointer<GetEffective>(get);
                if (NativeLibrary.TryGetExport(lib, "PowerSetActiveOverlayScheme", out var set))
                    _set = Marshal.GetDelegateForFunctionPointer<SetActive>(set);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException or BadImageFormatException)
            {
                _get = null;
                _set = null;
            }
        }
    }

    public PowerMode Read()
    {
        Bind();
        if (_get is null) return PowerMode.Unknown;
        try
        {
            return _get(out var scheme) == 0 ? PowerModes.FromGuid(scheme) : PowerMode.Unknown;
        }
        catch (Exception ex) when (ex is SEHException or MarshalDirectiveException or AccessViolationException) { return PowerMode.Unknown; }
    }

    public int Set(PowerMode mode)
    {
        Bind();
        if (_set is null) return -1;
        if (!PowerModes.Guids.TryGetValue(mode, out var guid)) return -2;   // not one of the three: refused here, whatever the caller says
        try { return unchecked((int)_set(ref guid)); }
        catch (Exception ex) when (ex is SEHException or MarshalDirectiveException) { return -3; }
    }
}
