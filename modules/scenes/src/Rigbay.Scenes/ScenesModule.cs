using Rigbay.Abstractions;

namespace Rigbay.Scenes;

/// <summary>
/// Game scenes: when a program you chose runs, other modules' commands run under a plan you approved once, and are put back when it ends. This module decides WHEN;
/// the host decides WHETHER (it runs only a plan whose hash equals the one approved, rate limits it, journals it and notifies). Read the skill <c>scenes-module</c> before changing it.
/// </summary>
public sealed class ScenesModule : IModule
{
    private readonly Dependencies? _fixed;
    private ScenesRuntime? _runtime;

    /// <summary>The host's constructor: sets nothing.</summary>
    public ScenesModule() { }

    /// <summary>Tests: the same module with fakes.</summary>
    internal ScenesModule(Dependencies deps) => _fixed = deps;

    internal ScenesRuntime? Runtime => _runtime;

    /// <inheritdoc />
    public ProbeResult Probe() => OperatingSystem.IsWindows() ? ProbeResult.Yes() : ProbeResult.No("probe.not-windows");

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _runtime = new ScenesRuntime(host, _fixed ?? Dependencies.Real());
        _runtime.Start();
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var runtime = _runtime;
        _runtime = null;
        runtime?.Stop();
    }

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host) => _runtime?.BuildPage();

    /// <summary>Tests: waits for the work the module started in the background.</summary>
    internal Task WhenIdleAsync() => _runtime?.WhenIdleAsync() ?? Task.CompletedTask;
}
