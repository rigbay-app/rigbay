namespace Rigbay.Abstractions;

// Level 1 of the module-contributes-a-page contract: a declarative page. No UI framework types here.
// The host renders it as setting rows, indexes header + description for search, and localises by key.
// Colours are SemanticColor names, icons are glyph names or paths, text is language keys (module-relative).

/// <summary>
/// The page of one module: groups of rows. The host adds the first row itself (the enable switch, with the name and
/// description from the manifest) and disables every group while the module is off.
/// </summary>
public sealed class PageModel
{
    /// <summary>A glyph name or a file path (relative to the module folder) for the navigation icon; null for the default icon.</summary>
    public string? Icon { get; init; }

    /// <summary>The rows, in groups.</summary>
    public IReadOnlyList<RowGroup> Groups { get; init; } = Array.Empty<RowGroup>();

    /// <summary>Links shown at the bottom ("Learn more").</summary>
    public IReadOnlyList<PageLink> Links { get; init; } = Array.Empty<PageLink>();

    /// <summary>What the dashboard bay shows for this module: a state line and up to two readings. Null shows only the state.</summary>
    public PageSummary? Summary { get; init; }

    /// <summary>All rows, depth first, with the children of expanders after their parent.</summary>
    public IEnumerable<Row> EnumerateRows()
    {
        foreach (var group in Groups)
            foreach (var row in group.Rows)
                foreach (var flat in Flatten(row))
                    yield return flat;
    }

    private static IEnumerable<Row> Flatten(Row row)
    {
        yield return row;
        if (row is ExpanderRow expander)
            foreach (var child in expander.Children)
                foreach (var flat in Flatten(child))
                    yield return flat;
    }
}

/// <summary>A titled list of rows.</summary>
/// <param name="HeaderKey">Module-relative language key of the group title.</param>
/// <param name="Rows">The rows.</param>
public sealed record RowGroup(string HeaderKey, IReadOnlyList<Row> Rows);

/// <summary>A link at the bottom of a page.</summary>
/// <param name="TextKey">Module-relative language key of the link text.</param>
/// <param name="Url">An http or https address.</param>
public sealed record PageLink(string TextKey, string Url);

/// <summary>What the dashboard bay shows next to the module's state.</summary>
/// <param name="StateLine">Short text such as "Profile Quiet" or "Proxy on :18081"; null for none. Module-relative key.</param>
/// <param name="Readings">
/// The one or two most important readings; more than two are ignored. The label the dashboard shows for each comes from the
/// <see cref="MetricDescriptor"/> the module registered with <see cref="IMetricsBus.Describe"/>; without one it shows the raw id.
/// </param>
public sealed record PageSummary(IObservableValue<LocalisedText>? StateLine = null, IReadOnlyList<MetricId>? Readings = null);

/// <summary>
/// Description of what a hardware-changing row does. The host shows <see cref="DescriptionKey"/> and asks for
/// confirmation before it applies the change, and offers "Revert" afterwards. This is a convenience for the user, not
/// a safety mechanism: the module must still refuse dangerous values itself.
/// </summary>
/// <param name="DescriptionKey">Module-relative key of the effect text, for example "Sets the fans to maximum. Noise will increase."</param>
/// <param name="Revertible">True when setting the previous value back (or calling <see cref="ButtonRow.RevertAsync"/>) undoes the change.</param>
public sealed record HardwareEffect(string DescriptionKey, bool Revertible = true)
{
    /// <summary>
    /// What kind of change this is; the host picks neutral wording from it. <see cref="EffectKind.Hardware"/> (default) says "Change your
    /// hardware?"; <see cref="EffectKind.SystemSetting"/> says "Change a system setting?" (a Windows proxy, a registry value that is not
    /// hardware). Additive: existing modules keep the hardware wording.
    /// </summary>
    public EffectKind Kind { get; init; } = EffectKind.Hardware;

    /// <summary>
    /// Opt-in, default false. A revert is a hardware write like any other, so by default the host asks again before it
    /// puts the previous value back, with the exact current and previous value in the question (Auto to Maximum is as
    /// much a change as Maximum to Auto). Set it to true only when every possible revert of this row is harmless for the
    /// machine (a lighting colour, a harmless label); the host then reverts without a question but its status line still
    /// names what was written. Never set it on a row whose previous value could be a higher fan, power or voltage setting
    /// than the current one.
    /// </summary>
    public bool SafeRevert { get; init; }

    /// <summary>
    /// Module-relative key of the effect text shown when the revert is asked about (for example "Puts the fans back to the
    /// previous mode."); null shows <see cref="DescriptionKey"/>.
    /// </summary>
    public string? RevertDescriptionKey { get; init; }
}

/// <summary>The kind of change a <see cref="HardwareEffect"/> describes; it only chooses the wording of the host's confirmation texts.</summary>
public enum EffectKind
{
    /// <summary>A write to the machine's hardware (fans, power limits, firmware). The default.</summary>
    Hardware = 0,
    /// <summary>A Windows or application setting that is not hardware (a proxy setting, a registry value).</summary>
    SystemSetting = 1,
}

/// <summary>How a value row applies the user's edit.</summary>
public enum ApplyMode
{
    /// <summary>The value is set as soon as the user changes the control (toggles and choices; numbers that are safe to change live).</summary>
    Immediate,

    /// <summary>
    /// The user edits a draft; nothing reaches the module until "Apply". The host checks the module's validation first,
    /// asks for confirmation when the row has a <see cref="HardwareEffect"/>, sets the value, and keeps the previous
    /// value so "Revert" can put it back. "Discard" drops the draft. Use it for every number, text or shortcut that
    /// writes to hardware.
    /// </summary>
    ApplyRevert,
}

/// <summary>Base of all rows. Header and description are module-relative language keys; the host indexes both for search.</summary>
/// <param name="HeaderKey">Module-relative language key of the row title.</param>
public abstract record Row(string HeaderKey)
{
    /// <summary>Module-relative language key of the one-line description, or null.</summary>
    public string? DescriptionKey { get; init; }

    /// <summary>Whether the user can use the control; the module may change it at any time.</summary>
    public IObservableValue<bool> Enabled { get; init; } = new ObservableValue<bool>(true);

    /// <summary>Set on a row that changes the state of the machine; see <see cref="Abstractions.HardwareEffect"/>.</summary>
    public HardwareEffect? HardwareEffect { get; init; }

    /// <summary>True when <see cref="HardwareEffect"/> is set. The host confirms and offers revert for such rows.</summary>
    public bool ChangesHardware => HardwareEffect is not null;
}

/// <summary>An on/off switch.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Value">The state.</param>
public sealed record ToggleRow(string HeaderKey, IObservableValue<bool> Value) : Row(HeaderKey);

/// <summary>One option of a <see cref="ChoiceRow"/>.</summary>
/// <param name="Id">Stable id, stored in <see cref="ChoiceRow.Value"/>.</param>
/// <param name="LabelKey">Module-relative key of the label.</param>
public sealed record ChoiceOption(string Id, string LabelKey);

/// <summary>A drop-down with a fixed list of options.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Options">The options (replace the row to change the list).</param>
/// <param name="Value">Id of the selected option.</param>
public sealed record ChoiceRow(string HeaderKey, IReadOnlyList<ChoiceOption> Options, IObservableValue<string> Value) : Row(HeaderKey);

/// <summary>A number box.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Value">The number.</param>
/// <param name="Minimum">Lowest allowed value; the host never sets a lower one.</param>
/// <param name="Maximum">Highest allowed value; the host never sets a higher one.</param>
/// <param name="Step">Increment of the spin buttons.</param>
public sealed record NumberRow(string HeaderKey, IObservableValue<double> Value, double Minimum, double Maximum, double Step = 1) : Row(HeaderKey)
{
    /// <summary>Unit shown after the number.</summary>
    public MetricUnit Unit { get; init; } = MetricUnit.None;

    /// <summary>Immediate or apply-or-revert.</summary>
    public ApplyMode Apply { get; init; } = ApplyMode.Immediate;

    /// <summary>Extra validation, called inside the fault boundary before a value is applied. Return an error text, or null when the value is fine.</summary>
    public Func<double, LocalisedText?>? Validate { get; init; }
}

/// <summary>A text box.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Value">The text.</param>
public sealed record TextRow(string HeaderKey, IObservableValue<string> Value) : Row(HeaderKey)
{
    /// <summary>Longest allowed text; 0 for no limit.</summary>
    public int MaxLength { get; init; }

    /// <summary>Module-relative key of the hint shown while empty, or null.</summary>
    public string? PlaceholderKey { get; init; }

    /// <summary>Immediate or apply-or-revert.</summary>
    public ApplyMode Apply { get; init; } = ApplyMode.Immediate;

    /// <summary>Extra validation, called inside the fault boundary before a value is applied. Return an error text, or null when the value is fine.</summary>
    public Func<string, LocalisedText?>? Validate { get; init; }
}

/// <summary>A key-combination picker; normally bound to <see cref="IHotkeyBinding.Gesture"/>.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Value">The gesture; null means unbound.</param>
public sealed record ShortcutRow(string HeaderKey, IObservableValue<HotkeyGesture?> Value) : Row(HeaderKey)
{
    /// <summary>Immediate or apply-or-revert.</summary>
    public ApplyMode Apply { get; init; } = ApplyMode.Immediate;
}

/// <summary>A button. An expected failure is a failed <see cref="ActionResult"/>; an exception faults the module.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="ButtonKey">Module-relative key of the button label (a verb).</param>
/// <param name="InvokeAsync">The action; runs inside the fault boundary.</param>
public sealed record ButtonRow(string HeaderKey, string ButtonKey, Func<CancellationToken, Task<ActionResult>> InvokeAsync) : Row(HeaderKey)
{
    /// <summary>Undoes the action; used for "Revert" when <see cref="Row.HardwareEffect"/> says it is revertible.</summary>
    public Func<CancellationToken, Task<ActionResult>>? RevertAsync { get; init; }

    /// <summary>
    /// Module-relative key of the label of the revert action (for example "Boost off"), shown in the question the host asks
    /// before it calls <see cref="RevertAsync"/> (see <see cref="Abstractions.HardwareEffect.SafeRevert"/>); null shows the generic word "Revert".
    /// </summary>
    public string? RevertButtonKey { get; init; }
}

/// <summary>A read-only text at the right (already localised by the module).</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Value">The text.</param>
public sealed record ValueRow(string HeaderKey, IObservableValue<string> Value) : Row(HeaderKey);

/// <summary>
/// A live number from the metrics bus, formatted by the host with the sample's unit. When the metric is withdrawn
/// (<see cref="IMetricsBus.Withdraw"/>) or was never published, the reading is "not available": the bus has no latest
/// sample for it, or the sample says <see cref="MetricSample.IsAvailable"/> is false. The host then shows its
/// "not available" text instead of a number.
/// </summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Metric">The metric to show.</param>
public sealed record ReadingRow(string HeaderKey, MetricId Metric) : Row(HeaderKey)
{
    /// <summary>Digits after the decimal point.</summary>
    public int Decimals { get; init; }

    /// <summary>Colour meaning; <see cref="SemanticColor.Auto"/> lets the host choose from the unit.</summary>
    public SemanticColor Color { get; init; } = SemanticColor.Auto;
}

/// <summary>A row that folds related rows.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Children">The folded rows.</param>
public sealed record ExpanderRow(string HeaderKey, IReadOnlyList<Row> Children) : Row(HeaderKey)
{
    /// <summary>Whether it starts open.</summary>
    public bool IsExpanded { get; init; }
}

/// <summary>An inline message (<see cref="Row.HeaderKey"/> is the title, <see cref="Row.DescriptionKey"/> the text).</summary>
/// <param name="Severity">How it looks.</param>
/// <param name="HeaderKey">Title key.</param>
public sealed record InfoRow(Severity Severity, string HeaderKey) : Row(HeaderKey)
{
    /// <summary>Whether the message is shown.</summary>
    public IObservableValue<bool> IsOpen { get; init; } = new ObservableValue<bool>(true);
}

/// <summary>
/// Level 2 inside level 1: the host asks <see cref="IModule.CreateCustomControl"/> for a control with this id and
/// places it in the page. For what a row cannot express (a curve editor, a graph, an activity log).
/// </summary>
/// <param name="ControlId">Id passed to <see cref="IModule.CreateCustomControl"/>.</param>
/// <param name="HeaderKey">Title key, or empty for none.</param>
public sealed record CustomRow(string ControlId, string HeaderKey = "") : Row(HeaderKey);
