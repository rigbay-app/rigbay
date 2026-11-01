namespace Rigbay.Abstractions;

/// <summary>
/// Answer to "does this machine support the module". Returning "no" is normal; throwing is a fault. The reason is a
/// module-relative language key shown to the user (<c>probe.no-service</c> becomes "The Acer gaming service is not installed").
/// </summary>
/// <param name="Supported">Whether the module can work here.</param>
/// <param name="ReasonKey">Module-relative language key of the reason when unsupported; empty when supported.</param>
/// <param name="ReasonArgs">Arguments for the reason text, or null.</param>
public sealed record ProbeResult(bool Supported, string ReasonKey = "", object[]? ReasonArgs = null)
{
    /// <summary>This machine is supported.</summary>
    public static ProbeResult Yes() => new(true);

    /// <summary>This machine is not supported, because of <paramref name="reasonKey"/>.</summary>
    /// <param name="reasonKey">Module-relative language key.</param>
    /// <param name="args">Arguments for the text.</param>
    public static ProbeResult No(string reasonKey, params object[] args) => new(false, reasonKey, args);
}

/// <summary>
/// The one type a module exports (named in <c>module.json</c>, <c>entry.type</c>); it needs a public parameterless
/// constructor that does nothing but set fields. Every call into it runs inside the host's fault boundary: an
/// exception or a timeout disables the module and is logged, and never reaches the host. Lifecycle:
/// <see cref="Probe"/> once per host start, then <see cref="Initialize"/> when enabled, then page calls, then
/// <see cref="Shutdown"/> when disabled or at exit. Enabling again calls <see cref="Initialize"/> on the same instance.
/// </summary>
public interface IModule
{
    /// <summary>
    /// Read-only check whether this machine supports the module (a service exists, a WMI class exists, ...). Called before
    /// <see cref="Initialize"/>, whether or not the module is enabled, with a 10 second limit. No host services exist yet.
    /// </summary>
    ProbeResult Probe();

    /// <summary>
    /// Called when the module is enabled and was probed as supported (10 second limit). Read settings, register hotkeys,
    /// tray items, commands and subscriptions, start timers.
    /// </summary>
    /// <param name="host">The services for this module; keep the reference.</param>
    void Initialize(IHostServices host);

    /// <summary>
    /// Called when the module is disabled and when the host exits (5 second limit). Stop timers and release handles. The
    /// host removes the module's hotkeys, tray items, commands and subscriptions afterwards.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Level 1: the declarative page, called after <see cref="Initialize"/> succeeded. Return null for a module without a
    /// page (a recorder, a sensor source). The host draws the enable row itself; do not add one.
    /// </summary>
    /// <param name="host">The services for this module.</param>
    PageModel? GetPageModel(IHostServices host) => null;

    /// <summary>
    /// Level 2: called by the host when it draws a <see cref="CustomRow"/>. Returns a control of the host's UI framework
    /// as <see cref="object"/> (for WinUI 3 a <c>Microsoft.UI.Xaml.UIElement</c>); the host checks the type and shows an
    /// error row otherwise. Build it in code. Do not offer a compiled XAML page: it cannot be loaded from a module folder.
    /// </summary>
    /// <param name="host">The services for this module.</param>
    /// <param name="controlId">The <see cref="CustomRow.ControlId"/> being drawn.</param>
    object? CreateCustomControl(IHostServices host, string controlId) => null;
}
