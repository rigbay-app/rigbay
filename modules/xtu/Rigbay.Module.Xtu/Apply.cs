using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

internal enum ApplyOrigin
{
    /// <summary>A hotkey. Asks for approval.</summary>
    Hotkey,
    /// <summary><c>rigbay xtu apply</c>. Asks for approval.</summary>
    Command,
    /// <summary>A tray item. Asks for approval.</summary>
    Tray,
    /// <summary>
    /// A row on the module page. The host has already shown the exact change and asked (the row carries a hardware effect), so
    /// this class does not ask again.
    /// </summary>
    PageConfirmed,
    /// <summary>
    /// Auto-switch by power source. The user's consent was given, with the host's confirmation, when they turned auto-switch on
    /// or chose the profile for a power source.
    /// </summary>
    Auto,
}

internal enum ApplyStatus
{
    /// <summary>Written and read back.</summary>
    Applied,
    /// <summary>The hardware already had these values; nothing was written.</summary>
    AlreadyActive,
    /// <summary>The user said no; nothing was written.</summary>
    Declined,
    /// <summary>Another apply is running; this request was dropped.</summary>
    Busy,
    /// <summary>The values are outside the safe ranges; refused before asking anyone.</summary>
    Invalid,
    /// <summary>XTU refused, could not be reached, or the write latch is on.</summary>
    Failed,
}

internal sealed record ApplyResult(ApplyStatus Status, XtuProfile Profile, XtuState? Before = null, XtuState? After = null, XtuException? Error = null);

/// <summary>
/// Everything that can write to the hardware goes through here: validate, read the current values, ask for approval where
/// the host has not asked already, apply through the client. A declined approval writes nothing.
/// </summary>
internal sealed class ApplyService
{
    public const string EffectKey = "effect.apply-profile";

    private readonly IXtuClient _client;
    private readonly IHardwareApproval _approval;
    private readonly ILog _log;
    private readonly WriteLatch _latch;
    private int _busy;

    public ApplyService(IXtuClient client, IHardwareApproval approval, ILog log, WriteLatch? latch = null)
    {
        _client = client;
        _approval = approval;
        _log = log;
        _latch = latch ?? WriteLatch.Off;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Raised after every attempt that was not dropped as busy, with who asked. Listeners must not throw.</summary>
    public event Action<ApplyResult, ApplyOrigin>? Completed;

    public async Task<ApplyResult> ApplyAsync(XtuProfile target, ApplyOrigin origin, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new ApplyResult(ApplyStatus.Busy, target);

        ApplyResult result;
        try
        {
            result = await RunAsync(target, origin, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }

        _log.Info($"apply [{target.Name}] {target.Describe()} by {origin}: {result.Status}"
                  + (result.Error is null ? "" : " (" + result.Error.Message + ")"));
        try { Completed?.Invoke(result, origin); }
        catch (Exception ex) { _log.Warn("apply: a listener threw", ex); }
        return result;
    }

    private async Task<ApplyResult> RunAsync(XtuProfile target, ApplyOrigin origin, CancellationToken ct)
    {
        try
        {
            Rules.Validate(target);
        }
        catch (XtuException ex)
        {
            return new ApplyResult(ApplyStatus.Invalid, target, Error: ex);
        }

        XtuState before;
        try
        {
            _latch.ThrowIfOn();
            before = await Task.Run(_client.ReadState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (XtuException ex)
        {
            return new ApplyResult(ApplyStatus.Failed, target, Error: ex);
        }

        if (target.Matches(before))
            return new ApplyResult(ApplyStatus.AlreadyActive, target, before, before);

        if (origin is ApplyOrigin.Hotkey or ApplyOrigin.Command or ApplyOrigin.Tray)
        {
            bool approved;
            try
            {
                approved = await _approval.ConfirmAsync(new HardwareEffect(EffectKey), Detail(target, before), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                approved = false;
            }
            if (!approved) return new ApplyResult(ApplyStatus.Declined, target, before);
        }

        try
        {
            var outcome = await Task.Run(() => _client.Apply(target), CancellationToken.None).ConfigureAwait(false);
            return new ApplyResult(outcome.AlreadyActive ? ApplyStatus.AlreadyActive : ApplyStatus.Applied, target, before, outcome.After);
        }
        catch (XtuException ex)
        {
            return new ApplyResult(ApplyStatus.Failed, target, before, Error: ex);
        }
    }

    /// <summary>The approval text: the exact values that will be written and the ones now in force.</summary>
    internal static LocalisedText Detail(XtuProfile target, XtuState now) =>
        LocalisedText.Of(target.Pl2Enable ? "approve.detail" : "approve.detail-pl2-off",
            target.Name, Fmt.N(target.Pl1), Fmt.N(target.Pl2), Fmt.N(target.Window), Fmt.Limits(now));
}
