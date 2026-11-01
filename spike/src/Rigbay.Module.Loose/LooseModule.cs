using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Rigbay.Abstractions;

namespace Rigbay.Module.Loose;

/// <summary>Approach 3: XAML text embedded in the module and parsed at run time with XamlReader.Load (no XAML compiler, no ms-appx).</summary>
public sealed class LooseModule : IRigbayModule
{
    public string Id => "spike.loose";
    public string Name => "Runtime XAML page";
    public string Group => "Monitoring";
    public Version Version => new(1, 0, 0);
    public ProbeResult Probe() => ProbeResult.Yes();

    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Spacing="14" MaxWidth="560" HorizontalAlignment="Left">
          <TextBlock Text="Sensors (sample, nothing is read)" FontWeight="SemiBold"/>
          <ListView SelectionMode="None" Width="360" HorizontalAlignment="Left">
            <ListViewItem Content="CPU package  58 C"/>
            <ListViewItem Content="GPU core  47 C"/>
            <ListViewItem Content="Fan 1  2400 rpm"/>
          </ListView>
          <TextBlock TextWrapping="Wrap" Text="Parsed with XamlReader.Load from a string embedded in the module DLL. Styles and theme resources resolve from the host application."/>
        </StackPanel>
        """;

    public object CreatePage(IHostServices host) => (UIElement)XamlReader.Load(Xaml);
}
