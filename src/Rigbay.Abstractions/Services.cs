namespace Rigbay.Abstractions;

/// <summary>
/// What the host offers a module; one instance per module, passed to <see cref="IModule.Initialize"/>. No member
/// takes or returns a UI framework or Windows type. Every service is safe to call from any thread. Callbacks given to
/// a service run inside the module's fault boundary.
/// </summary>
public interface IHostServices
{
    /// <summary>Id of the module this instance belongs to.</summary>
    string ModuleId { get; }

    /// <summary>The module's install folder (read-only; do not write here).</summary>
    string ModuleDirectory { get; }

    /// <summary>The module's data folder, <c>%LOCALAPPDATA%\Rigbay\modules\&lt;id&gt;</c>; created on first use.</summary>
    string DataDirectory { get; }

    /// <summary>Version of the host application (informational; use <see cref="HostApi.Current"/> for compatibility).</summary>
    Version HostVersion { get; }

    /// <summary>Cancelled when <see cref="IModule.Shutdown"/> starts. Pass it to background work.</summary>
    CancellationToken StopToken { get; }

    /// <summary>Per-module settings file.</summary>
    ISettingsStore Settings { get; }

    /// <summary>Language lookup for this module's keys.</summary>
    ILocalisation Localisation { get; }

    /// <summary>The log.</summary>
    ILog Log { get; }

    /// <summary>Hotkey registry.</summary>
    IHotkeyService Hotkeys { get; }

    /// <summary>Tray menu items.</summary>
    ITrayService Tray { get; }

    /// <summary>Command-line verbs.</summary>
    ICommandRegistry Commands { get; }

    /// <summary>Notifications.</summary>
    IToastService Toast { get; }

    /// <summary>The metrics bus.</summary>
    IMetricsBus Metrics { get; }

    /// <summary>Asks the user to confirm a hardware write that does not go through a page row.</summary>
    IHardwareApproval Approval { get; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread inside the host's fault boundary. This is the only way a module
    /// reaches the UI thread: never use a dispatcher or synchronisation context of your own, because an exception in a
    /// raw dispatcher callback ends the whole process. Returns immediately (the action runs later).
    /// </summary>
    /// <param name="action">The work.</param>
    void RunOnUi(Action action);

    /// <summary>
    /// Asks the host to call <see cref="IModule.GetPageModel"/> again and replace the module's page, for example after a
    /// list the page shows has changed (a profile was created or deleted). Safe to call from any thread, returns
    /// immediately, calls made while a rebuild is pending or running are coalesced into one more rebuild, and it does
    /// nothing while the module is not running. Without a call, the page is built once when the module is enabled.
    /// A fault in <c>GetPageModel</c> during a refresh disables the module like any other fault.
    /// </summary>
    void RequestPageRefresh();

    /// <summary>
    /// True while the host shows this module's page (set by the app through the host). A module that polls hardware can
    /// poll at the fast rate while this is true and at a slow rate otherwise: <c>PageVisible.Changed</c> says when to
    /// switch. Combine it with <see cref="IMetricsBus.SubscriberCount"/>: poll fast when the page is visible
    /// <i>or</i> someone subscribes to a metric you publish. False while the module is off. Read the value in
    /// <see cref="IModule.Initialize"/> (it is the current state), do not assume true. The module cannot set it:
    /// a set from the module is ignored. <c>Changed</c> runs synchronously on the thread that sets the state, usually the
    /// UI thread, inside this module's fault boundary: hand slow work to a background thread. A host that does not support
    /// the signal returns a value that stays true.
    /// </summary>
    IObservableValue<bool> PageVisible => ConstantValue<bool>.True;
}

/// <summary>A value that is fixed: setting it does nothing. Used as the default of members a host may not implement.</summary>
internal sealed class ConstantValue<T>(T value) : IObservableValue<T>
{
    public static readonly ConstantValue<bool> True = new(true);

    public T Value { get => value; set { } }
    public event Action? Changed { add { } remove { } }
}

/// <summary>
/// Confirmation of a hardware write. Rows marked with a <see cref="HardwareEffect"/> go through the host
/// automatically; a module uses this for writes it starts itself (after a hotkey, at start-up). If the host has no
/// way to ask (no window, command-line run) the answer is no.
/// </summary>
public interface IHardwareApproval
{
    /// <summary>Shows what will happen and asks the user.</summary>
    /// <param name="effect">What the write does, by language key.</param>
    /// <param name="detail">Extra text, for example the exact value, or null.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>True when the user approved.</returns>
    Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default);
}

/// <summary>One typed settings object per module, in <c>%LOCALAPPDATA%\Rigbay\modules\&lt;id&gt;\settings.json</c>.</summary>
public interface ISettingsStore
{
    /// <summary>
    /// Returns the stored object, or <c>new T()</c> (the defaults) when there is no file. Properties missing from the file
    /// keep their defaults and unknown ones are ignored. A corrupt file is renamed to <c>settings.json.corrupt-&lt;time&gt;</c>,
    /// logged, and defaults are returned. Never throws for file problems.
    /// </summary>
    /// <typeparam name="T">A class with a public parameterless constructor and public settable properties.</typeparam>
    T Load<T>() where T : class, new();

    /// <summary>Like <see cref="Load{T}()"/>, and tells which version wrote the file (0 when there is no usable file), for migrations.</summary>
    /// <typeparam name="T">The settings class.</typeparam>
    /// <param name="storedVersion">The version found in the file.</param>
    T Load<T>(out int storedVersion) where T : class, new();

    /// <summary>Writes atomically (temporary file in the same folder, then replace).</summary>
    /// <typeparam name="T">The settings class.</typeparam>
    /// <param name="value">What to store.</param>
    /// <param name="version">Your own schema version, returned by <see cref="Load{T}(out int)"/>.</param>
    /// <exception cref="IOException">The disk refused the write.</exception>
    void Save<T>(T value, int version = 1) where T : class;
}

/// <summary>Language lookup. Keys in module code are module-relative; the prefix <c>module.&lt;id&gt;.</c> is added.</summary>
public interface ILocalisation
{
    /// <summary>Code of the current language, for example <c>en</c> or <c>vi</c>.</summary>
    string Language { get; }

    /// <summary>Text for a key: current language, then English, then the full key itself.</summary>
    /// <param name="key">Module-relative key.</param>
    string T(string key);

    /// <summary>Like <see cref="T"/> with <c>string.Format</c> arguments (invariant culture). A broken placeholder in a translation falls back to English.</summary>
    /// <param name="key">Module-relative key.</param>
    /// <param name="args">Values for the placeholders.</param>
    string F(string key, params object[] args);

    /// <summary>Resolves a <see cref="LocalisedText"/>.</summary>
    /// <param name="text">The text.</param>
    string Resolve(LocalisedText text);

    /// <summary>Raised after the user switched language (on the thread that switched it).</summary>
    event Action? LanguageChanged;
}

/// <summary>Severity of a log entry.</summary>
public enum LogLevel
{
    /// <summary>Detail for diagnosing.</summary>
    Debug,
    /// <summary>Normal events.</summary>
    Info,
    /// <summary>Something unexpected that was handled.</summary>
    Warn,
    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>The log: <c>%LOCALAPPDATA%\Rigbay\logs\rigbay.log</c>. Do not log secrets.</summary>
public interface ILog
{
    /// <summary>Writes one entry; the host adds time and module id.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="message">The text (English; the log is not localised).</param>
    /// <param name="exception">An exception to record with its stack, or null.</param>
    void Write(LogLevel level, string message, Exception? exception = null);
}

/// <summary>Shortcuts for <see cref="ILog"/>.</summary>
public static class LogExtensions
{
    /// <summary>Writes a debug entry.</summary>
    /// <param name="log">The log.</param>
    /// <param name="message">The text.</param>
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);

    /// <summary>Writes an info entry.</summary>
    /// <param name="log">The log.</param>
    /// <param name="message">The text.</param>
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    /// <summary>Writes a warning.</summary>
    /// <param name="log">The log.</param>
    /// <param name="message">The text.</param>
    /// <param name="exception">The exception, or null.</param>
    public static void Warn(this ILog log, string message, Exception? exception = null) => log.Write(LogLevel.Warn, message, exception);

    /// <summary>Writes an error.</summary>
    /// <param name="log">The log.</param>
    /// <param name="message">The text.</param>
    /// <param name="exception">The exception, or null.</param>
    public static void Error(this ILog log, string message, Exception? exception = null) => log.Write(LogLevel.Error, message, exception);
}

/// <summary>Hotkey registry. Data only: the app registers the gestures with the OS.</summary>
public interface IHotkeyService
{
    /// <summary>Registers a named action. The user's rebinding of the same action id from an earlier run is applied.</summary>
    /// <param name="registration">The action.</param>
    /// <exception cref="ArgumentException">The action id is invalid or already registered by this module.</exception>
    IHotkeyBinding Register(HotkeyRegistration registration);
}

/// <summary>A tray menu entry (data only; the app draws the menu, grouped by module).</summary>
public sealed class TrayItem
{
    /// <summary>Creates an item.</summary>
    /// <param name="id">Id inside the module (<c>[a-z0-9-]</c>).</param>
    /// <param name="textKey">Module-relative language key of the label.</param>
    /// <param name="invoke">Called when the user picks the item, in the module's fault boundary.</param>
    public TrayItem(string id, string textKey, Action invoke)
    {
        Id = id;
        TextKey = textKey;
        Invoke = invoke;
    }

    /// <summary>Id inside the module.</summary>
    public string Id { get; }

    /// <summary>Module-relative language key of the label.</summary>
    public string TextKey { get; }

    /// <summary>Called when the item is picked.</summary>
    public Action Invoke { get; }

    /// <summary>A glyph name or file path for an icon, or null for none. Plain data, never a UI object.</summary>
    public string? Icon { get; init; }

    /// <summary>Whether the item shows a check mark that <see cref="Checked"/> controls.</summary>
    public bool Checkable { get; init; }

    /// <summary>Whether the item is shown.</summary>
    public IObservableValue<bool> Visible { get; } = new ObservableValue<bool>(true);

    /// <summary>Whether the item can be picked.</summary>
    public IObservableValue<bool> Enabled { get; } = new ObservableValue<bool>(true);

    /// <summary>The check mark, when <see cref="Checkable"/>.</summary>
    public IObservableValue<bool> Checked { get; } = new ObservableValue<bool>(false);
}

/// <summary>Tray menu registry.</summary>
public interface ITrayService
{
    /// <summary>Adds an item.</summary>
    /// <param name="item">The item.</param>
    /// <exception cref="ArgumentException">The id is invalid or already used by this module.</exception>
    void Add(TrayItem item);

    /// <summary>Removes an item of this module.</summary>
    /// <param name="id">The item id.</param>
    void Remove(string id);
}

/// <summary>Kind of a notification.</summary>
public enum ToastKind
{
    /// <summary>Information.</summary>
    Info,
    /// <summary>Something worked.</summary>
    Success,
    /// <summary>Needs attention.</summary>
    Warning,
    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>A notification.</summary>
/// <param name="Title">Title (module-relative key).</param>
/// <param name="Body">Body (module-relative key).</param>
/// <param name="Kind">Kind.</param>
public sealed record ToastMessage(LocalisedText Title, LocalisedText Body, ToastKind Kind = ToastKind.Info);

/// <summary>Notifications; the app supplies the implementation.</summary>
public interface IToastService
{
    /// <summary>Shows a notification. Does nothing when the host cannot show one.</summary>
    /// <param name="message">The notification.</param>
    void Show(ToastMessage message);
}

/// <summary>What a command handler receives.</summary>
/// <param name="Args">The words after <c>rigbay &lt;module&gt; &lt;verb&gt;</c>.</param>
/// <param name="CancellationToken">Cancelled when the caller gives up or the host stops.</param>
public sealed record CommandContext(IReadOnlyList<string> Args, CancellationToken CancellationToken);

/// <summary>
/// Outcome of a command. Exit codes: 0 ok, 1 failed, 2 bad usage. Output goes to standard output; for a failure
/// to standard error.
/// </summary>
/// <param name="ExitCode">0, 1 or 2.</param>
/// <param name="Output">Plain text (already localised by the module).</param>
public sealed record CommandResult(int ExitCode, string Output)
{
    /// <summary>Exit code 0 with text.</summary>
    /// <param name="output">The text.</param>
    public static CommandResult Ok(string output = "") => new(0, output);

    /// <summary>Exit code 1: the command ran and failed (an expected failure; throwing faults the module instead).</summary>
    /// <param name="output">The explanation.</param>
    public static CommandResult Failed(string output) => new(1, output);

    /// <summary>Exit code 2: wrong arguments; the text should say how to call it.</summary>
    /// <param name="output">The usage text.</param>
    public static CommandResult Usage(string output) => new(2, output);
}

/// <summary>A command implementation.</summary>
/// <param name="context">Arguments and cancellation.</param>
public delegate Task<CommandResult> CommandHandler(CommandContext context);

/// <summary><c>rigbay &lt;module&gt; &lt;verb&gt; [args]</c>.</summary>
public interface ICommandRegistry
{
    /// <summary>Adds a verb to this module.</summary>
    /// <param name="verb">Lowercase, digits and dashes, starting with a letter.</param>
    /// <param name="descriptionKey">Module-relative language key of the one-line help.</param>
    /// <param name="handler">The implementation.</param>
    /// <exception cref="ArgumentException">The verb is invalid or already registered by this module.</exception>
    void Register(string verb, string descriptionKey, CommandHandler handler);
}

/// <summary>Convenience for synchronous commands.</summary>
public static class CommandRegistryExtensions
{
    /// <summary>Adds a verb with a synchronous handler.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="verb">The verb.</param>
    /// <param name="descriptionKey">Module-relative key of the help text.</param>
    /// <param name="handler">The implementation.</param>
    public static void Register(this ICommandRegistry registry, string verb, string descriptionKey, Func<CommandContext, CommandResult> handler) =>
        registry.Register(verb, descriptionKey, ctx => Task.FromResult(handler(ctx)));
}
