using Rigbay.Abstractions;

namespace Rigbay.Recorder.Tests;

/// <summary>
/// The real <see cref="RecorderModule"/> with a neutral made-up folder (no user name in a picture) and the fake shell of its tests, so "Open
/// folder" opens nothing. It records nothing unless someone presses Start; the metrics it offers are what the other demo modules publish.
/// </summary>
public sealed class ReleasedRecorderDemo : IModule
{
    private readonly RecorderModule _inner = new(null, new FakeShell(), () => @"C:\Rigbay-demo\recordings");

    public ProbeResult Probe() => _inner.Probe();
    public void Initialize(IHostServices host) => _inner.Initialize(host);
    public PageModel? GetPageModel(IHostServices host) => _inner.GetPageModel(host);
    public void Shutdown() => _inner.Shutdown();
}
