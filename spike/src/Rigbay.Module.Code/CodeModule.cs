using System.Runtime.Loader;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;

namespace Rigbay.Module.Code;

/// <summary>Approach 1: the module builds a WinUI UIElement tree in C#.</summary>
public sealed class CodeModule : IRigbayModule
{
    public string Id => "spike.code";
    public string Name => "Code page";
    public string Group => "Other";
    public Version Version => new(1, 0, 0);
    public ProbeResult Probe() => ProbeResult.Yes();

    public object CreatePage(IHostServices host)
    {
        var alc = AssemblyLoadContext.GetLoadContext(typeof(CodeModule).Assembly)?.Name ?? "?";
        var winui = typeof(UIElement).Assembly;
        var copies = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "Microsoft.WinUI");

        var power = new Slider { Minimum = 20, Maximum = 120, Value = 65, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var value = new TextBlock { Text = "65 W" };
        power.ValueChanged += (_, e) => value.Text = $"{(int)e.NewValue} W";

        var throwButton = new Button { Content = "Throw from a click handler", Name = "ThrowButton" };
        throwButton.Click += (_, _) => throw new InvalidOperationException("module click handler threw on purpose");

        var panel = new StackPanel { Spacing = 14, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Power limit (sample control, writes nothing)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(power);
        panel.Children.Add(value);
        panel.Children.Add(new ToggleSwitch { Header = "Apply at start", IsOn = true });
        panel.Children.Add(throwButton);
        panel.Children.Add(new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Text = $"Built in C#.\nLoad context: {alc}\nMicrosoft.WinUI copies in process: {copies}\nUIElement from: {winui.Location}"
        });
        return panel;
    }
}
