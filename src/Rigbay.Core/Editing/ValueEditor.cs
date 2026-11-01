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
/// <see cref="SubmitAsync(T, CancellationToken)"/> (rows with <see cref="ApplyMode.Immediate"/>) or <see cref="ApplyAsync(CancellationToken)"/> after the user
/// presses Apply (rows with <see cref="ApplyMode.ApplyRevert"/>). Applying checks the draft, asks for confirmation when
/// the row <see cref="Row.ChangesHardware"/>, sets the module's value inside the fault boundary, and remembers the
/// previous value for <see cref="Revert"/>.
/// </summary>
public sealed class ValueEditor<T> : IDisposable
{
    private readonly ModuleEntry _entry;
    private readonly Row _row;
    private readonly IObservableValue<T> _value;
    private readonly Func<T, string?> _validate;
    private readonly Func<T, string> _format;
    private bool _disposed;
    private readonly object _gate = new();
    private T _draft;
    private bool _hasPrevious;
    private bool _userEdited;
    private T _previous = default!;

    internal ValueEditor(ModuleEntry entry, Row row, IObservableValue<T> value, ApplyMode mode, Func<T, string?> validate, Func<T, string>? format = null)
    {
        _entry = entry;
        _row = row;
        _value = value;
        Mode = mode;
        _validate = validate;
        _format = format ?? (v => v?.ToString() ?? "");
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

    /// <summary>Applies the draft. The confirmation for a hardware change shows the new value as text (<see cref="DetailFormatter"/>).</summary>
    public Task<EditResult> ApplyAsync(CancellationToken cancellationToken = default) => ApplyCoreAsync(null, cancellationToken);

    /// <summary>Applies the draft; <paramref name="detail"/> replaces the default text the confirmation shows (for example "Auto → Maximum").</summary>
    public Task<EditResult> ApplyAsync(string? detail, CancellationToken cancellationToken = default) => ApplyCoreAsync(detail, cancellationToken);

    /// <summary>Like <see cref="SubmitAsync(T, CancellationToken)"/> with an explicit confirmation detail.</summary>
    public Task<EditResult> SubmitAsync(T value, string? detail, CancellationToken cancellationToken = default)
    {
        SetDraft(value);
        return ApplyCoreAsync(detail, cancellationToken);
    }

    /// <summary>Text of a value for the confirmation. The default is set by <see cref="RowEditors"/> per row kind (the value as the user sees it).</summary>
    public Func<T, string>? DetailFormatter { get; set; }

    private async Task<EditResult> ApplyCoreAsync(string? detail, CancellationToken cancellationToken)
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
            string? shown = detail;
            if (shown is null)
            {
                try { shown = (DetailFormatter ?? _format)(draft); }
                catch (Exception ex) { _entry.Log.Warn("approval detail could not be formatted: " + ex.Message); }
            }
            var ok = await _entry.Host.RequestApprovalTextAsync(_entry, effect, shown, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Puts the value from before the last applied change back, and asks first when that is a hardware change: a revert is a
    /// write like any other, so the question names the exact current and previous value ("Fan mode: Maximum → Auto").
    /// The exception is a row whose <see cref="HardwareEffect.SafeRevert"/> is true: it reverts without a question, and
    /// <see cref="EditResult.Message"/> still names what was written. Rows that do not change hardware revert without a
    /// question. A declined revert changes nothing and keeps <see cref="CanRevert"/>.
    /// </summary>
    public async Task<EditResult> RevertAsync(CancellationToken cancellationToken = default)
    {
        T previous;
        lock (_gate)
        {
            if (!_hasPrevious) return new EditResult(EditOutcome.NothingToDo);
            previous = _previous;
        }
        var current = _value.Value;

        if (NeedsRevertApproval(current, previous, out var effect))
        {
            var detail = RevertDetail(current, previous);
            var ok = await _entry.Host.RequestApprovalTextAsync(_entry, effect, detail, cancellationToken, isRevert: true).ConfigureAwait(false);
            if (!ok) return new EditResult(EditOutcome.Declined, _entry.Host.Languages.T(_entry.Host.ApprovalPrompt is null ? "core.approval.no-prompt" : "core.approval.declined"));

            // What the user approved is "current → previous". If either moved while the question was open, write nothing.
            bool stale;
            lock (_gate) stale = !_hasPrevious || !EqualityComparer<T>.Default.Equals(_previous, previous);
            if (stale) return new EditResult(EditOutcome.NothingToDo);
            if (!EqualityComparer<T>.Default.Equals(_value.Value, current))
                return new EditResult(EditOutcome.Declined, _entry.Host.Languages.T("core.revert.changed"));
        }
        return CommitRevert(current, previous);
    }

    /// <summary>
    /// The synchronous revert. It cannot ask the user, so it only reverts what needs no question: a row that does not change
    /// hardware, or one with <see cref="HardwareEffect.SafeRevert"/>. For any other hardware row it writes nothing and returns
    /// <see cref="EditOutcome.Declined"/> with a message; use <see cref="RevertAsync"/>, which asks.
    /// </summary>
    public EditResult Revert()
    {
        T previous;
        lock (_gate)
        {
            if (!_hasPrevious) return new EditResult(EditOutcome.NothingToDo);
            previous = _previous;
        }
        var current = _value.Value;
        if (NeedsRevertApproval(current, previous, out var revertEffect))
            return new EditResult(EditOutcome.Declined, _entry.Host.Languages.T(revertEffect.Kind == EffectKind.SystemSetting ? "core.revert.needs-approval-setting" : "core.revert.needs-approval"));
        return CommitRevert(current, previous);
    }

    // A write happens only when the value really changes; reverting to the value that is already set needs no question.
    private bool NeedsRevertApproval(T current, T previous, out HardwareEffect effect)
    {
        effect = _row.HardwareEffect!;
        return _row.HardwareEffect is { SafeRevert: false } && !EqualityComparer<T>.Default.Equals(current, previous);
    }

    private string? RevertDetail(T current, T previous)
    {
        try
        {
            var fmt = DetailFormatter ?? _format;
            return $"{_entry.Localisation.T(_row.HeaderKey)}: {fmt(current)} → {fmt(previous)}";
        }
        catch (Exception ex)
        {
            _entry.Log.Warn("approval detail could not be formatted: " + ex.Message);
            return null;
        }
    }

    private EditResult CommitRevert(T current, T previous)
    {
        if (!_entry.Invoke("revert " + _row.HeaderKey, () => _value.Value = previous))
            return new EditResult(EditOutcome.Faulted, _entry.Host.Languages.T("core.edit.failed"));
        lock (_gate)
        {
            _hasPrevious = false;
            _userEdited = false;
            _draft = previous;
        }
        Changed?.Invoke();

        // The status line names what was written, for every hardware row (the question was skipped for SafeRevert rows).
        string? message = null;
        if (_row.ChangesHardware)
        {
            try
            {
                var fmt = DetailFormatter ?? _format;
                message = _entry.Host.Languages.F("core.revert.done", _entry.Localisation.T(_row.HeaderKey), fmt(previous), fmt(current));
            }
            catch (Exception ex) { _entry.Log.Warn("revert message could not be formatted: " + ex.Message); }
        }
        return new EditResult(EditOutcome.Applied, message);
    }

    /// <summary>Stops following the module's value. Safe to call more than once; the editor still works afterwards but no longer follows the module.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _value.Changed -= OnModuleChanged;
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

/// <summary>
/// Creates the editor that belongs to a row kind, wiring in the row's own limits and the module's Validate. There is one
/// editor per row instance: asking again for the same row returns the same editor, so a page can be rebuilt without
/// piling up subscriptions. The editors are disposed when the module stops or faults, and before it is enabled again.
/// </summary>
public static class RowEditors
{
    public static ValueEditor<bool> For(ModuleEntry entry, ToggleRow row) =>
        entry.GetOrCreateEditor(row, () => new ValueEditor<bool>(entry, row, row.Value, ApplyMode.Immediate, _ => null,
            v => entry.Host.Languages.T(v ? "core.value.on" : "core.value.off")));

    public static ValueEditor<string> For(ModuleEntry entry, ChoiceRow row) =>
        entry.GetOrCreateEditor(row, () => new ValueEditor<string>(entry, row, row.Value, ApplyMode.Immediate,
            v => row.Options.Any(o => o.Id == v) ? null : entry.Host.Languages.T("core.edit.unknown-option"),
            v => row.Options.FirstOrDefault(o => o.Id == v) is { } option ? entry.Localisation.T(option.LabelKey) : v));

    public static ValueEditor<double> For(ModuleEntry entry, NumberRow row) =>
        entry.GetOrCreateEditor(row, () => new ValueEditor<double>(entry, row, row.Value, row.Apply, v =>
        {
            if (double.IsNaN(v) || v < row.Minimum || v > row.Maximum)
                return entry.Host.Languages.F("core.edit.out-of-range", row.Minimum, row.Maximum);
            return row.Validate?.Invoke(v) is { IsEmpty: false } text ? entry.Localisation.Resolve(text) : null;
        }, v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    public static ValueEditor<string> For(ModuleEntry entry, TextRow row) =>
        entry.GetOrCreateEditor(row, () => new ValueEditor<string>(entry, row, row.Value, row.Apply, v =>
        {
            if (row.MaxLength > 0 && v.Length > row.MaxLength) return entry.Host.Languages.F("core.edit.too-long", row.MaxLength);
            return row.Validate?.Invoke(v) is { IsEmpty: false } text ? entry.Localisation.Resolve(text) : null;
        }, v => v));

    public static ValueEditor<HotkeyGesture?> For(ModuleEntry entry, ShortcutRow row) =>
        entry.GetOrCreateEditor(row, () => new ValueEditor<HotkeyGesture?>(entry, row, row.Value, row.Apply, _ => null,
            v => v is { } g ? g.ToString() : entry.Host.Languages.T("core.value.unbound")));

    /// <summary>Runs a button: confirmation first when it changes hardware, then the action in the fault boundary.</summary>
    /// <param name="entry">The module.</param>
    /// <param name="row">The button.</param>
    /// <param name="cancellationToken">Cancels the wait and the action.</param>
    /// <param name="detail">Text for the confirmation (for example the exact change); null shows the button label.</param>
    public static async Task<ActionResult> InvokeButtonAsync(ModuleEntry entry, ButtonRow row, CancellationToken cancellationToken = default, string? detail = null)
    {
        if (row.HardwareEffect is { } effect && !await entry.Host.RequestApprovalTextAsync(entry, effect, detail ?? entry.Localisation.T(row.ButtonKey), cancellationToken).ConfigureAwait(false))
            return ActionResult.Fail("core.approval.declined");
        var (completed, result) = await entry.InvokeAsync("button " + row.HeaderKey, () => row.InvokeAsync(cancellationToken)).ConfigureAwait(false);
        return completed && result is not null ? result : ActionResult.Fail("core.edit.failed");
    }

    /// <summary>
    /// Runs a button's revert action, when the row has one. Like every call that changes hardware it asks first: the question
    /// names the button and its revert (<see cref="ButtonRow.RevertButtonKey"/>) unless <paramref name="detail"/> gives the
    /// exact text. A declined question returns the failed result "core.approval.declined" and calls nothing. A row whose
    /// <see cref="HardwareEffect.SafeRevert"/> is true does not ask; a successful result without a message then gets one that
    /// names the revert, so the status line says what was done.
    /// </summary>
    /// <param name="entry">The module.</param>
    /// <param name="row">The button.</param>
    /// <param name="cancellationToken">Cancels the wait and the action.</param>
    /// <param name="detail">Text for the question; null builds it from the button and its revert label.</param>
    public static async Task<ActionResult> RevertButtonAsync(ModuleEntry entry, ButtonRow row, CancellationToken cancellationToken = default, string? detail = null)
    {
        if (row.RevertAsync is not { } revert) return ActionResult.Ok();
        var revertLabel = row.RevertButtonKey is { Length: > 0 } key ? entry.Localisation.T(key) : entry.Host.Languages.T("core.revert.generic");
        if (row.HardwareEffect is { SafeRevert: false } effect)
        {
            var shown = detail ?? $"{entry.Localisation.T(row.HeaderKey)}: {revertLabel}";
            if (!await entry.Host.RequestApprovalTextAsync(entry, effect, shown, cancellationToken, isRevert: true).ConfigureAwait(false))
                return ActionResult.Fail("core.approval.declined");
        }
        var (completed, result) = await entry.InvokeAsync("revert " + row.HeaderKey, () => revert(cancellationToken)).ConfigureAwait(false);
        if (!completed || result is null) return ActionResult.Fail("core.edit.failed");
        if (row.HardwareEffect is { SafeRevert: true } && result.Succeeded && result.Message is not { IsEmpty: false })
            return ActionResult.Ok(LocalisedText.Of("core.revert.button-done", entry.Localisation.T(row.HeaderKey), revertLabel));
        return result;
    }
}
