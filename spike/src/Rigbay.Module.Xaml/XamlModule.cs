using Rigbay.Abstractions;

namespace Rigbay.Module.Xaml;

/// <summary>Approach 2: a Page with its own compiled XAML (SamplePage.xaml) inside the module assembly.</summary>
public sealed class XamlModule : IRigbayModule
{
    public string Id => "spike.xaml";
    public string Name => "Compiled XAML page";
    public string Group => "Other";
    public Version Version => new(1, 0, 0);
    public ProbeResult Probe() => ProbeResult.Yes();
    public object CreatePage(IHostServices host) => new SamplePage();
}
