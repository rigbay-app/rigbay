using Rigbay.Abstractions;

namespace Rigbay.PowerRules;

/// <summary>
/// Changes the internal panel's refresh rate and the Windows power mode when the PC goes on battery or is plugged in. Nothing happens on its
/// own until the owner approves the exact rule set once; every change is read back; the values from before the first automatic change are
/// remembered and put back when automatic switching is turned off or the module is. Read the skill <c>powerrules-module</c> before changing it.
/// </summary>
public sealed class PowerRulesModule : IModule
{
    private Dependencies? _deps;
    private bool _ownsDeps;   // the real dependencies are made again for the next Initialize: Stop disposes the power listener
    private PowerRulesRuntime? _runtime;

    /// <summary>The host's constructor: sets nothing (the real dependencies are made in <see cref="Probe"/>).</summary>
    public PowerRulesModule() { }

    /// <summary>Tests: the same module with fakes.</summary>
    internal PowerRulesModule(Dependencies deps) => _deps = deps;

    internal PowerRulesRuntime? Runtime => _runtime;

    /// <inheritdoc />
    public ProbeResult Probe()
    {
        MakeDependencies();
        return _deps!.Source.HasBattery() ? ProbeResult.Yes() : ProbeResult.No("probe.no-battery");
    }

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        MakeDependencies();
        _runtime = new PowerRulesRuntime(host, _deps!);
        _runtime.Start();
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var runtime = _runtime;
        _runtime = null;
        runtime?.Stop();
        if (_ownsDeps) { _deps = null; _ownsDeps = false; }
    }

    private void MakeDependencies()
    {
        if (_deps is not null) return;
        _deps = Dependencies.Real();
        _ownsDeps = true;
    }

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host) => _runtime?.BuildPage();

    /// <summary>Tests: waits for the work the module started in the background.</summary>
    internal Task WhenIdleAsync() => _runtime?.WhenIdleAsync() ?? Task.CompletedTask;
}
