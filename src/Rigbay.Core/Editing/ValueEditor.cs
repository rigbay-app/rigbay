using Rigbay.Abstractions;

namespace Rigbay.Core;

public enum EditOutcome
{
    /// <summary>The value reached the module.</summary>
    Applied,
    /// <summary>The draft failed a check (range, length, option, the module's Validate); see <see cref="EditResult.Message"/>.</summary>
    Invalid,
    /// <summary>The user did not confirm a hardware change (or nobody could be asked); the draft is kept.</summary>
    Declined,
    /// <summary>The module threw while applying; it is now faulted.</summary>
    Faulted,
    /// <summary>The draft equals the current value.</summary>
    NothingToDo,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Message">Text for the user in the current language, or null.</param>
public sealed record EditResult(EditOutcome Outcome, string? Message = null);

/// <summary>
/// The apply-or-revert model of one value row. The UI binds a control to <see cref="Draft"/> and calls
/// <see cref="SubmitAsync"/> (rows with <see cref="ApplyMode.Immediate"/>) or <see cref="ApplyAsync"/> after the user
/// presses Apply (rows with <see cref="ApplyMode.ApplyRevert"/>). Applying checks the draft, asks for confirmation when
/// the row <see cref="Row.ChangesHardware"/>, sets the module's value inside the fault boundary, and remembers the
/// previous value for <see cref="Revert"/>.
/// </summary>
public sealed class ValueEditor<T>
{
    private readonly ModuleEntry _entry;
    private readonly Row _row;
    private readonly IObservableValue<T> _value;
    private readonly Func<T, string?> _validate;
    private readonly object _gate = new();
    private T _draft;
    private bool _hasPrevious;
    private bool _userEdited;
    private T _previous = default!;

    internal ValueEditor(ModuleEntry entry, Row row, IObservableValue<T> value, ApplyMode mode, Func<T, string?> validate)
    {
        _entry = entry;
        _row = row;
        _value = value;
        Mode = mode;
        _validate = validate;
        _draft = value.Value;
        // A change made by the module while the user is not editing moves the draft along with it.
        _value.Changed += OnModuleChanged;
    }

    public ApplyMode Mode { get; }

    /// <summary>Raised when <see cref="Draft"/>, <see cref="IsDirty"/> or <see cref="CanRevert"/> changed (any thread).</summary>
    public event Action? Changed;

    public T Draft { get { lock (_gate) return _draft; } }

    /// <summary>True when the draft differs from the module's current value (shows Apply and Discard).</summary>
    public bool IsDirty { get { lock (_gate) return !EqualityComparer<T>.Default.Equals(_draft, _value.Value); } }

    /// <summary>True when an applied change can be taken back (shows Revert).</summary>
    public bool CanRevert { get { lock (_gate) return _hasPrevious; } }

    /// <summary>The check result for the current draft: null when it is fine.</summary>
    public string? Error => _validate(Draft);

    public void SetDraft(T value)
    {
        lock (_gate) { _draft = value; _userEdited = true; }
        Changed?.Invoke();
    }

    /// <summary>Drops the draft and shows the module's value again.</summary>
    public void Discard()
    {
        lock (_gate) { _draft = _value.Value; _userEdited = false; }
        Changed?.Invoke();
    }

    /// <summary>For immediate rows: sets the draft and applies it.</summary>
    public Task<EditResult> SubmitAsync(T value, CancellationToken cancellationToken = default)
    {
        SetDraft(value);
        return ApplyAsync(cancellationToken);
    }

    public async Task<EditResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        T draft;
        lock (_gate) draft = _draft;
        if (EqualityComparer<T>.Default.Equals(draft, _value.Value)) return new EditResult(EditOutcome.NothingToDo);

        string? problem;
        try { problem = _validate(draft); }
        catch (Exception ex)
        {
            _entry.FaultNow("Validate", ex);
            return new EditResult(EditOutcome.Faulted, _entry.Host.Languages.T("core.edit.failed"));
        }
        if (problem is not null) return new EditResult(EditOutcome.Invalid, problem);

        if (_row.HardwareEffect is { } effect)
        {
            var ok = await _entry.Host.RequestApprovalAsync(_entry, effect, null, cancellationToken).ConfigureAwait(false);
            if (!ok) return new EditResult(EditOutcome.Declined, _entry.Host.Languages.T(_entry.Host.ApprovalPrompt is null ? "core.approval.no-prompt" : "core.approval.declined"));
        }

        var before = _value.Value;
        if (!_entry.Invoke("set " + _row.HeaderKey, () => _value.Value = draft))
            return new EditResult(EditOutcome.Faulted, _entry.Host.Languages.T("core.edit.failed"));

        lock (_gate)
        {
            _previous = before;
            _hasPrevious = true;
            _userEdited = false;
        }
        Changed?.Invoke();
        return new EditResult(EditOutcome.Applied);
    }

    /// <summary>Puts the value from before the last applied change back. A revert is not asked about again.</summary>
    public EditResult Revert()
    {
        T previous;
        lock (_gate)
        {
            if (!_hasPrevious) return new EditResult(EditOutcome.NothingToDo);
            previous = _previous;
        }
        if (!_entry.Invoke("revert " + _row.HeaderKey, () => _value.Value = previous))
            return new EditResult(EditOutcome.Faulted, _entry.Host.Languages.T("core.edit.failed"));
        lock (_gate)
        {
            _hasPrevious = false;
            _userEdited = false;
            _draft = previous;
        }
        Changed?.Invoke();
        return new EditResult(EditOutcome.Applied);
    }

    private void OnModuleChanged()
    {
        lock (_gate)
        {
            // Follow the module unless the user has an unapplied draft of their own.
            if (!_userEdited) _draft = _value.Value;
        }
        Changed?.Invoke();
    }

}

/// <summary>Creates the editor that belongs to a row kind, wiring in the row's own limits and the module's Validate.</summary>
public static class RowEditors
{
    public static ValueEditor<bool> For(ModuleEntry entry, ToggleRow row) =>
        new(entry, row, row.Value, ApplyMode.Immediate, _ => null);

    public static ValueEditor<string> For(ModuleEntry entry, ChoiceRow row) =>
        new(entry, row, row.Value, ApplyMode.Immediate,
            v => row.Options.Any(o => o.Id == v) ? null : entry.Host.Languages.T("core.edit.unknown-option"));

    public static ValueEditor<double> For(ModuleEntry entry, NumberRow row) =>
        new(entry, row, row.Value, row.Apply, v =>
        {
            if (double.IsNaN(v) || v < row.Minimum || v > row.Maximum)
                return entry.Host.Languages.F("core.edit.out-of-range", row.Minimum, row.Maximum);
            return row.Validate?.Invoke(v) is { } text ? entry.Localisation.Resolve(text) : null;
        });

    public static ValueEditor<string> For(ModuleEntry entry, TextRow row) =>
        new(entry, row, row.Value, row.Apply, v =>
        {
            if (row.MaxLength > 0 && v.Length > row.MaxLength) return entry.Host.Languages.F("core.edit.too-long", row.MaxLength);
            return row.Validate?.Invoke(v) is { } text ? entry.Localisation.Resolve(text) : null;
        });

    public static ValueEditor<HotkeyGesture?> For(ModuleEntry entry, ShortcutRow row) =>
        new(entry, row, row.Value, row.Apply, _ => null);

    /// <summary>Runs a button: confirmation first when it changes hardware, then the action in the fault boundary.</summary>
    public static async Task<ActionResult> InvokeButtonAsync(ModuleEntry entry, ButtonRow row, CancellationToken cancellationToken = default)
    {
        if (row.HardwareEffect is { } effect && !await entry.Host.RequestApprovalAsync(entry, effect, null, cancellationToken).ConfigureAwait(false))
            return ActionResult.Fail("core.approval.declined");
        var (completed, result) = await entry.InvokeAsync("button " + row.HeaderKey, () => row.InvokeAsync(cancellationToken)).ConfigureAwait(false);
        return completed && result is not null ? result : ActionResult.Fail("core.edit.failed");
    }

    /// <summary>Runs a button's revert action, when the row has one.</summary>
    public static async Task<ActionResult> RevertButtonAsync(ModuleEntry entry, ButtonRow row, CancellationToken cancellationToken = default)
    {
        if (row.RevertAsync is not { } revert) return ActionResult.Ok();
        var (completed, result) = await entry.InvokeAsync("revert " + row.HeaderKey, () => revert(cancellationToken)).ConfigureAwait(false);
        return completed && result is not null ? result : ActionResult.Fail("core.edit.failed");
    }
}
