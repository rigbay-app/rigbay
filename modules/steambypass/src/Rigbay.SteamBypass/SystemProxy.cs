using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Rigbay.SteamBypass;

/// <summary>Reads and writes the one Windows setting the module changes: the current user's proxy script address (<c>AutoConfigURL</c>).</summary>
internal interface ISystemProxyStore
{
    /// <summary>The current value, or null when there is none.</summary>
    string? ReadPacUrl();

    /// <summary>Sets the value.</summary>
    void WritePacUrl(string url);

    /// <summary>Removes the value (no error when it is not there).</summary>
    void DeletePacUrl();

    /// <summary>Tells running programs that the setting changed.</summary>
    void NotifyChanged();
}

/// <summary>
/// The setting in the registry: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\AutoConfigURL</c> in production.
/// The key path is a constructor argument so the tests can run the same code against a scratch key.
/// </summary>
internal sealed class RegistryProxyStore : ISystemProxyStore
{
    /// <summary>The key Windows reads the proxy settings from, under HKEY_CURRENT_USER.</summary>
    public const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const string ValueName = "AutoConfigURL";
    private readonly string _subKey;
    private readonly bool _notifyWinInet;

    /// <param name="subKey">Key path under HKEY_CURRENT_USER.</param>
    /// <param name="notifyWinInet">Whether <see cref="NotifyChanged"/> calls WinINet. False for a scratch key: nobody reads it.</param>
    public RegistryProxyStore(string subKey = InternetSettingsKey, bool notifyWinInet = true)
    {
        _subKey = subKey;
        _notifyWinInet = notifyWinInet;
    }

    /// <summary>True when this store points at the real Windows setting.</summary>
    public bool IsRealSetting => string.Equals(_subKey, InternetSettingsKey, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? ReadPacUrl()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <inheritdoc />
    public void WritePacUrl(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true);
        key.SetValue(ValueName, url, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void DeletePacUrl()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public void NotifyChanged()
    {
        if (!_notifyWinInet) return;
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);   // INTERNET_OPTION_SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);   // INTERNET_OPTION_REFRESH
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
}

/// <summary>What was there before the module wrote its address: the value to put back.</summary>
/// <param name="Ours">The address the module wrote.</param>
/// <param name="Previous">The value that was there before, or null when there was none.</param>
internal sealed record ProxyState(string Ours, string? Previous);

/// <summary>Where the previous value is kept between runs (so a crash can still be undone).</summary>
internal interface IProxyStateStore
{
    /// <summary>The saved state, or null when there is none or it cannot be read.</summary>
    ProxyState? Load();

    /// <summary>Saves the state; it must be on disk when this returns.</summary>
    void Save(ProxyState state);

    /// <summary>Forgets the state.</summary>
    void Delete();
}

/// <summary>The state in a small JSON file in the module's data folder, written to a temporary file first and then moved over the old one.</summary>
internal sealed class FileProxyStateStore : IProxyStateStore
{
    private readonly string _path;

    public FileProxyStateStore(string path) => _path = path;

    /// <inheritdoc />
    public ProxyState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var state = JsonSerializer.Deserialize<ProxyState>(File.ReadAllText(_path));
            return state is { Ours.Length: > 0 } ? state : null;
        }
        catch (Exception) { return null; }
    }

    /// <inheritdoc />
    public void Save(ProxyState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, _path, overwrite: true);
    }

    /// <inheritdoc />
    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception) { /* a state file that stays is harmless: the next restore finds the value is not ours and removes it */ }
    }
}

/// <summary>How a restore ended.</summary>
internal enum RestoreOutcome
{
    /// <summary>The setting held our address and the previous value (or none) was put back.</summary>
    Restored,

    /// <summary>The setting does not hold our address (never set, already restored, or changed by someone else): nothing was touched.</summary>
    NothingToRestore,

    /// <summary>The registry could not be read or written. The saved state is kept so the next start can try again.</summary>
    Failed,
}

/// <summary>Result of <see cref="SystemProxyManager.Restore"/>.</summary>
internal readonly record struct RestoreResult(RestoreOutcome Outcome, string? Error = null);

/// <summary>
/// Applies and restores the proxy script address with the rules of the standalone app: the previous value is saved before the setting
/// is written; a restore only touches the setting when it still holds our address (it never undoes a change someone else made);
/// restoring twice is harmless.
/// </summary>
internal sealed class SystemProxyManager
{
    private readonly ISystemProxyStore _store;
    private readonly IProxyStateStore _state;
    private readonly string _prefix;

    /// <param name="store">The Windows setting.</param>
    /// <param name="state">The saved previous value.</param>
    /// <param name="pacUrl">The address this module writes, for example <c>http://127.0.0.1:18081/proxy.pac</c>.</param>
    public SystemProxyManager(ISystemProxyStore store, IProxyStateStore state, string pacUrl)
    {
        _store = store;
        _state = state;
        PacUrl = pacUrl;
        // Ownership is the scheme, host and port: "http://127.0.0.1:18081/". Another program's script on another port is never ours.
        int slash = pacUrl.IndexOf('/', pacUrl.IndexOf("://", StringComparison.Ordinal) + 3);
        _prefix = slash < 0 ? pacUrl + "/" : pacUrl[..(slash + 1)];
    }

    /// <summary>The address written by <see cref="Apply"/>.</summary>
    public string PacUrl { get; }

    /// <summary>True when <paramref name="value"/> is an address this module wrote (now or in an earlier run).</summary>
    public bool IsOurs(string? value) => value is not null && value.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the Windows setting holds our address right now.</summary>
    public bool IsApplied()
    {
        try { return IsOurs(_store.ReadPacUrl()); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Saves the previous value and writes our address. When our address is already there (a crashed earlier run) the saved value is kept.
    /// Throws when the registry cannot be written; in that case nothing changed and the state file is not left behind.
    /// </summary>
    public void Apply()
    {
        var current = _store.ReadPacUrl();
        bool alreadyOurs = IsOurs(current);
        bool savedHere = false;
        if (!alreadyOurs || _state.Load() is null)
        {
            _state.Save(new ProxyState(PacUrl, alreadyOurs ? null : current));
            savedHere = !alreadyOurs;
        }
        try
        {
            _store.WritePacUrl(PacUrl);
        }
        catch
        {
            if (savedHere) _state.Delete();
            throw;
        }
        try { _store.NotifyChanged(); } catch (Exception) { /* programs notice at their next start; the value is written */ }
    }

    /// <summary>
    /// Puts the previous value back, but only what this module set: the setting must still hold our address <b>and</b> the module's own
    /// saved state must exist (it is written just before the setting and removed just after it is restored). An address that looks like
    /// ours but was set by another program, for example a running standalone Steam Bypass, is never touched. Never throws.
    /// </summary>
    public RestoreResult Restore()
    {
        try
        {
            var current = _store.ReadPacUrl();
            var state = _state.Load();
            if (!IsOurs(current))
            {
                _state.Delete();   // the saved value is stale: somebody else changed the setting since
                return new RestoreResult(RestoreOutcome.NothingToRestore);
            }
            if (state is null) return new RestoreResult(RestoreOutcome.NothingToRestore);   // looks like ours, but this module did not set it
            var previous = state.Previous;
            if (!string.IsNullOrEmpty(previous)) _store.WritePacUrl(previous);
            else _store.DeletePacUrl();
            _state.Delete();
            try { _store.NotifyChanged(); } catch (Exception) { }
            return new RestoreResult(RestoreOutcome.Restored);
        }
        catch (Exception ex)
        {
            return new RestoreResult(RestoreOutcome.Failed, ex.Message);
        }
    }
}
