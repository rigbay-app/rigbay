namespace Rigbay.Abstractions;

// Level 1 of the module-contributes-a-page contract: a declarative page. No UI framework types here.
// The host renders it as setting rows, indexes header + description for search, and localises by key.

/// <summary>A value shared between module and host. Either side may set it; Changed fires on whichever thread set it.</summary>
public interface IObservableValue<T>
{
    T Value { get; set; }
    event Action? Changed;
}

public sealed class ObservableValue<T> : IObservableValue<T>
{
    T _v;
    public ObservableValue(T initial) => _v = initial;
    public T Value
    {
        get => _v;
        set { if (!Equals(_v, value)) { _v = value; Changed?.Invoke(); } }
    }
    public event Action? Changed;
}

public enum Severity { Info, Success, Warning, Error }

public sealed class PageModel
{
    public string Title { get; init; } = "";
    /// <summary>First row of every module page. Groups below it are disabled while it is off.</summary>
    public ToggleRow? Enable { get; init; }
    public List<RowGroup> Groups { get; init; } = new();
    public List<LinkItem> Links { get; init; } = new();
}

public sealed record RowGroup(string Header, IReadOnlyList<Row> Rows);
public sealed record LinkItem(string Text, string Url);

/// <summary>Header and Description are literal text in the spike; the product would pass language keys.</summary>
public abstract record Row(string Header, string Description = "");

public sealed record ToggleRow(string Header, string Description, IObservableValue<bool> Value) : Row(Header, Description);
public sealed record ChoiceRow(string Header, string Description, IReadOnlyList<string> Options, IObservableValue<string> Value) : Row(Header, Description);
public sealed record NumberRow(string Header, string Description, double Min, double Max, double Step, IObservableValue<double> Value) : Row(Header, Description);
public sealed record TextRow(string Header, string Description, IObservableValue<string> Value) : Row(Header, Description);
public sealed record ButtonRow(string Header, string Description, string ButtonText, Action Click) : Row(Header, Description);
/// <summary>Read-only value shown at the right.</summary>
public sealed record ValueRow(string Header, string Description, IObservableValue<string> Value) : Row(Header, Description);
/// <summary>A live reading (metrics bus later): Value is the formatted number, Unit e.g. "°C".</summary>
public sealed record ReadingRow(string Header, string Description, string Unit, IObservableValue<string> Value) : Row(Header, Description);
public sealed record ExpanderRow(string Header, string Description, IReadOnlyList<Row> Rows) : Row(Header, Description);
public sealed record InfoRow(Severity Severity, string Header, string Description = "") : Row(Header, Description);
/// <summary>Level 2 inside level 1: the host asks the module for a control by id (IPageModelProvider.CreateCustomControl).</summary>
public sealed record CustomRow(string ControlId, string Header, string Description = "") : Row(Header, Description);

/// <summary>Implemented by modules that contribute a declarative page. CreateCustomControl returns a UIElement (checked by the host).</summary>
public interface IPageModelProvider
{
    PageModel GetPageModel(IHostServices host);
    object? CreateCustomControl(IHostServices host, string controlId);
}
