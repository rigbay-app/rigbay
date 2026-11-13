using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// The context of one verb that the host started on behalf of another module (host API 0.1.4). It flows with the verb's async work
/// (<see cref="AsyncLocal{T}"/>) and does two things: a verb started this way cannot start another call (no loops), and, for a step of an approved
/// plan, it lets the host answer the target module's own "may I write?" question without a dialog, under tight limits.
/// </summary>
internal sealed class CallScope
{
    private static readonly AsyncLocal<CallScope?> CurrentScope = new();
    private readonly object _gate = new();
    private readonly List<string> _notes = new();
    private int _remaining;
    private bool _closed;

    public CallScope(string targetModuleId, bool isPlanStep, EffectKind? declaredKind = null, int maxAnswers = 3)
    {
        TargetModuleId = targetModuleId;
        IsPlanStep = isPlanStep;
        DeclaredKind = declaredKind;
        _remaining = maxAnswers;
    }

    /// <summary>The scope that is current on this async flow, or null.</summary>
    public static CallScope? Current { get => CurrentScope.Value; set => CurrentScope.Value = value; }

    /// <summary>True when a host call is running on this flow and has not ended: a module must not start another from inside it.</summary>
    public static bool InsideOpenCall => CurrentScope.Value is { IsClosed: false };

    public string TargetModuleId { get; }
    public bool IsPlanStep { get; }

    /// <summary>The kind of change the step's verb declared, or null for a verb that declared nothing.</summary>
    public EffectKind? DeclaredKind { get; }

    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Ends the scope: later questions (a task the verb left running) go to the normal dialog.</summary>
    public void Close() { lock (_gate) _closed = true; }

    /// <summary>What the target module asked while it ran in this scope and the host answered for the owner.</summary>
    public IReadOnlyList<string> Notes { get { lock (_gate) return _notes.ToArray(); } }

    /// <summary>
    /// Answers "yes" when the question comes from the target module of an open plan step, its kind equals the declared kind (a verb that declared
    /// nothing accepts any, and the note says so) and fewer than the allowed number were answered. Otherwise false and the caller asks normally.
    /// </summary>
    public bool TryAnswer(string askingModuleId, EffectKind askedKind, string effectText, string? detail)
    {
        lock (_gate)
        {
            if (_closed || !IsPlanStep || _remaining <= 0 || !string.Equals(askingModuleId, TargetModuleId, StringComparison.Ordinal)) return false;
            if (DeclaredKind is { } declared && declared != askedKind) return false;
            _remaining--;
            _notes.Add(string.IsNullOrEmpty(detail) ? effectText : effectText + " | " + detail + (DeclaredKind is null ? " (verb declared no effect kind)" : ""));
            return true;
        }
    }
}
