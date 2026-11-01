using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;

namespace Rigbay.Module.Rows;

/// <summary>
/// Level 1 (declarative page model, no WinUI in the model) plus level 2 (one custom control, built in code,
/// asked for by id). Nothing here writes to hardware: every value is a sample.
/// </summary>
public sealed class RowsModule : IRigbayModule, IPageModelProvider
{
    public string Id => "spike.rows";
    public string Name => "Cooling sample";
    public string Group => "Power";
    public Version Version => new(0, 3, 0);
    public ProbeResult Probe() => ProbeResult.Yes();
    public object? CreatePage(IHostServices host) => null;   // declarative: the host builds the page from GetPageModel

    readonly ObservableValue<bool> _enabled = new(true);
    readonly ObservableValue<string> _cpuTemp = new("--"), _cpuFan = new("--");
    Timer? _timer;
    int _tick;

    public PageModel GetPageModel(IHostServices host)
    {
        // Readings change on a thread-pool thread: the host must marshal to the UI thread itself.
        _timer ??= new Timer(_ =>
        {
            _tick++;
            _cpuTemp.Value = (58 + 6 * Math.Sin(_tick / 4.0)).ToString("0");
            _cpuFan.Value = (2400 + 300 * Math.Sin(_tick / 6.0)).ToString("0");
        }, null, 0, 500);

        var mode = new ObservableValue<string>("Auto");
        var perf = new ObservableValue<string>("Default");
        var pl1 = new ObservableValue<double>(45);
        var delay = new ObservableValue<double>(2);
        var name = new ObservableValue<string>("Quiet nights");
        var applied = new ObservableValue<string>("Not applied");

        return new PageModel
        {
            Title = Name,
            Enable = new ToggleRow("Enable cooling sample", "Fan and performance rows (sample, writes nothing)", _enabled),
            Groups =
            {
                new RowGroup("Cooling", new Row[]
                {
                    new ChoiceRow("Fan mode", "Auto, maximum or a custom curve", new[] { "Auto", "Max", "Custom" }, mode),
                    new CustomRow("fancurve", "Fan curve", "Temperature on the bottom, fan speed up the side"),
                    new ExpanderRow("Advanced", "Timing and naming", new Row[]
                    {
                        new NumberRow("Fan ramp delay (s)", "Wait before the fan follows a temperature change", 0, 30, 1, delay),
                        new TextRow("Profile name", "Shown on the dashboard", name),
                    }),
                }),
                new RowGroup("Performance", new Row[]
                {
                    new ChoiceRow("Mode", "Quiet, default, performance or turbo", new[] { "Quiet", "Default", "Performance", "Turbo" }, perf),
                    new NumberRow("Power limit PL1 (W)", "Sustained package power", 15, 65, 1, pl1),
                    new InfoRow(Severity.Warning, "Writes need your approval", "Applying changes the machine; the host asks first."),
                    new ButtonRow("Apply", "Send the values above", "Apply", () => { host.Log(Id, $"Apply clicked: mode={mode.Value} perf={perf.Value} pl1={pl1.Value}"); applied.Value = "Applied (sample)"; }),
                    new ValueRow("Last result", "", applied),
                }),
                new RowGroup("Readings", new Row[]
                {
                    new ReadingRow("CPU temperature", "Package, updates twice a second", "°C", _cpuTemp),
                    new ReadingRow("CPU fan", "", "rpm", _cpuFan),
                }),
            },
            Links = { new LinkItem("Learn more", "https://github.com/rigbay-app") },
        };
    }

    public object? CreateCustomControl(IHostServices host, string controlId)
    {
        if (controlId != "fancurve") return null;
        var canvas = new Canvas { Width = 320, Height = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var line = new Polyline
        {
            StrokeThickness = 2,
            // theme resource from the host application: proves styles are shared across the load-context boundary
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Points = { new(0, 110), new(80, 100), new(160, 60), new(240, 20), new(320, 10) }
        };
        canvas.Children.Add(line);
        return canvas;
    }
}
