namespace Rigbay.Abstractions;

/// <summary>
/// A value shared between a module and the host. Either side may set it.
/// <para><b>Thread rule:</b> <see cref="Changed"/> is raised synchronously on the thread that set the value, after the
/// new value is stored. The host marshals to the UI thread; a module that handles <see cref="Changed"/> (to write to
/// hardware, for example) must expect to run on the UI thread's call path and hand slow work to a background thread.
/// Handlers are called inside the host's fault boundary when the host is the setter.</para>
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public interface IObservableValue<T>
{
    /// <summary>The current value.</summary>
    T Value { get; set; }

    /// <summary>Raised after <see cref="Value"/> changed to a different value (compared with the default equality).</summary>
    event Action? Changed;
}

/// <summary>A ready implementation of <see cref="IObservableValue{T}"/>.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class ObservableValue<T> : IObservableValue<T>
{
    private readonly object _gate = new();
    private T _value;

    /// <summary>Creates the value with its initial content.</summary>
    /// <param name="initial">The first value; no event is raised for it.</param>
    public ObservableValue(T initial) => _value = initial;

    /// <inheritdoc />
    public T Value
    {
        get { lock (_gate) return _value; }
        set
        {
            lock (_gate)
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
            }
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public event Action? Changed;
}

/// <summary>
/// Text for the user, as a language key plus optional arguments. Keys written by a module are module-relative
/// (<c>probe.no-service</c>); the host adds the <c>module.&lt;id&gt;.</c> prefix when it resolves them.
/// </summary>
/// <param name="Key">The language key.</param>
/// <param name="Args">Values for <c>{0}</c>, <c>{1}</c>, ... in the text.</param>
public readonly record struct LocalisedText(string Key, object[]? Args = null)
{
    /// <summary>Creates a text from a key and arguments.</summary>
    /// <param name="key">The language key.</param>
    /// <param name="args">The arguments.</param>
    public static LocalisedText Of(string key, params object[] args) => new(key, args);

    /// <summary>A key without arguments.</summary>
    /// <param name="key">The language key.</param>
    public static implicit operator LocalisedText(string key) => new(key);
}

/// <summary>Result of an expected operation (a button, a command step). Unexpected failures are exceptions instead.</summary>
/// <param name="Succeeded">Whether the operation worked.</param>
/// <param name="Message">Optional text for the user (shown as a message next to the control).</param>
public sealed record ActionResult(bool Succeeded, LocalisedText? Message = null)
{
    /// <summary>Success, with an optional message.</summary>
    /// <param name="message">Text for the user, or null.</param>
    public static ActionResult Ok(LocalisedText? message = null) => new(true, message);

    /// <summary>A failure the user should read about; it does not fault the module.</summary>
    /// <param name="key">The module-relative language key of the explanation.</param>
    /// <param name="args">Arguments for the text.</param>
    public static ActionResult Fail(string key, params object[] args) => new(false, new LocalisedText(key, args));
}

/// <summary>Fixed colour meanings. The host maps them to theme brushes; no colour value ever crosses the contract.</summary>
public enum SemanticColor
{
    /// <summary>The host chooses from the unit of the reading (temperature warm, power another hue, and so on).</summary>
    Auto,
    /// <summary>Text colour, no emphasis.</summary>
    Neutral,
    /// <summary>Good or running.</summary>
    Ok,
    /// <summary>Needs attention.</summary>
    Warning,
    /// <summary>Failed or dangerous.</summary>
    Error,
    /// <summary>The user's accent colour (primary action or selection only).</summary>
    Accent,
    /// <summary>Hue family for temperatures.</summary>
    Temperature,
    /// <summary>Hue family for power and energy.</summary>
    Power,
    /// <summary>Hue family for speeds (fans, clocks, rates).</summary>
    Speed,
    /// <summary>Hue family for load and usage.</summary>
    Load,
}

/// <summary>Severity of a message.</summary>
public enum Severity
{
    /// <summary>Neutral information.</summary>
    Info,
    /// <summary>Something worked.</summary>
    Success,
    /// <summary>Needs attention.</summary>
    Warning,
    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>The closed set of states a module shows on the dashboard rack (the "bay").</summary>
public enum BayState
{
    /// <summary>Enabled, initialised and working.</summary>
    Ready,
    /// <summary>Supported on this machine but switched off by the user.</summary>
    Off,
    /// <summary>This machine cannot use the module (probe said no, or its host API range does not match); there is a reason.</summary>
    Unsupported,
    /// <summary>The manifest is invalid, or the module threw or timed out; there is a reason.</summary>
    Failed,
    /// <summary>A newer version of the module is available (set by the installer, not by the module).</summary>
    UpdateAvailable,
    /// <summary>An install, update or removal waits for a host restart.</summary>
    RestartRequired,
}
