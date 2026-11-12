namespace Rigbay.Abstractions;

// Level 1 of the module-contributes-a-page contract: a declarative page. No UI framework types here.
// The host renders it as setting rows, indexes header + description for search, and localises by key.
// Colours are SemanticColor names, icons are glyph names or paths, text is language keys (module-relative).

/// <summary>
/// The page of one module: an optional main control and groups of rows. The host draws the header itself (the module's name, its state
/// and <see cref="PageSummary.StateLine"/>, the enable switch) and disables everything below it while the module is off.
/// </summary>
public sealed class PageModel
{
    /// <summary>
    /// The module's main job, drawn large in a card right under the page header and before the readings: a profile picker, the fan mode, a
    /// bypass switch, a Start button. A <see cref="ChoiceRow"/> with 2 to 5 short options becomes a segmented control (a drop-down when the
    /// window is too narrow or the options are many or long), a <see cref="ToggleRow"/> a switch with its state words, a <see cref="ButtonRow"/>
    /// an accent button, any other value row a normal row inside that card (an <see cref="ExpanderRow"/>, <see cref="InfoRow"/> or
    /// <see cref="CustomRow"/> is refused by the validator). Same editor, approval and Revert as in a group; do not repeat the row in a group.
    /// Optional: null keeps the page as it is. Host API 0.1.3.
    /// </summary>
    public Row? Primary { get; init; }

    /// <summary>A glyph name or a file path (relative to the module folder) for the navigation icon; null for the default icon.</summary>
    public string? Icon { get; init; }

    /// <summary>The rows, in groups.</summary>
    public IReadOnlyList<RowGroup> Groups { get; init; } = Array.Empty<RowGroup>();

    /// <summary>Links shown at the bottom ("Learn more").</summary>
    public IReadOnlyList<PageLink> Links { get; init; } = Array.Empty<PageLink>();

    /// <summary>What the dashboard bay shows for this module: a state line and up to two readings. Null shows only the state.</summary>
    public PageSummary? Summary { get; init; }

    /// <summary>
    /// All rows, depth first: <see cref="Primary"/> first when there is one, then the groups; an expander is followed by its
    /// <see cref="ExpanderRow.HeaderRow"/> and then its children.
    /// </summary>
    public IEnumerable<Row> EnumerateRows()
    {
        if (Primary is { } primary)
            foreach (var flat in Flatten(primary))
                yield return flat;
        foreach (var group in Groups)
            foreach (var row in group.Rows)
                foreach (var flat in Flatten(row))
                    yield return flat;
    }

    private static IEnumerable<Row> Flatten(Row row)
    {
        yield return row;
        if (row is ExpanderRow expander)
        {
            if (expander.HeaderRow is { } head)
                foreach (var flat in Flatten(head))
                    yield return flat;
            foreach (var child in expander.Children)
                foreach (var flat in Flatten(child))
                    yield return flat;
        }
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

    /// <summary>
    /// Module-relative key of a longer explanation. The host shows it as the tooltip (and the accessible help text) of a small info glyph after
    /// the header instead of a line under it: keep <see cref="DescriptionKey"/> for a fact the header does not say (about 70 characters at
    /// most) and put the why and the how here. Indexed for search like the description. Null for none. Host API 0.1.3.
    /// </summary>
    public string? HelpKey { get; init; }

    /// <summary>
    /// A live state line under the header that the module changes while the page is open ("Connected · handshake 15 s ago", "Set to Maximum
    /// at 14:02"). Shown after the description; an empty text (<see cref="LocalisedText.IsEmpty"/>) shows nothing and takes no room.
    /// Resolved like any module text (a <see cref="Text.Literal"/> key is allowed). Every row kind draws it, an <see cref="ExpanderRow"/> in
    /// its closed header, so the state of each item reads without opening it. Null for none. Host API 0.1.3.
    /// </summary>
    public IObservableValue<LocalisedText>? Line { get; init; }
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

    /// <summary>
    /// Module-relative key of a question the host asks before it runs <see cref="InvokeAsync"/>, for an action that cannot be undone (deleting a
    /// profile). The text is the dialog's title and the action button wears <see cref="ButtonKey"/>'s label; nothing runs when the answer is no.
    /// <c>{0}</c> in the text is the header of the <see cref="ExpanderRow"/> that holds the row (the object it belongs to, such as a profile
    /// name), or the row's own header outside an expander: "Delete {0}?" reads "Delete Gaming?". Ignored on a row with a
    /// <see cref="Row.HardwareEffect"/>, whose approval already asks. Null for none. Host API 0.1.3.
    /// </summary>
    public string? ConfirmKey { get; init; }
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

    // The members below steer the visuals the host draws from a reading (a sparkline in the row, a tile at the top of the page,
    // a trend chart). All are optional and additive: a row that sets none of them is drawn from its metric's unit alone.

    /// <summary>Whether the reading becomes a tile at the top of the page. <see cref="TileHint.Auto"/> lets the host pick up to four from the page.</summary>
    public TileHint Tile { get; init; } = TileHint.Auto;

    /// <summary>
    /// Which trend chart the reading belongs to: a module-relative language key naming the group ("chart.power"), shared by every row
    /// that should be drawn together. Null groups by unit (temperatures together, power together); an empty string keeps the reading out of every chart.
    /// </summary>
    public string? ChartGroupKey { get; init; }

    /// <summary>
    /// Lowest value of the reading's natural range (for example 0 for a temperature gauge). With <see cref="RangeMax"/> it fixes the scale of
    /// the tile meter; null lets the host choose from the unit (0 to 100 for a percent, 0 to 100 °C for a temperature).
    /// </summary>
    public double? RangeMin { get; init; }

    /// <summary>Highest value of the natural range; see <see cref="RangeMin"/>. Must be above <see cref="RangeMin"/> when both are set.</summary>
    public double? RangeMax { get; init; }

    /// <summary>
    /// <see cref="ChartRole.Series"/> (default): a reading drawn as data. <see cref="ChartRole.Reference"/>: a limit or a target the data is read
    /// against (PL1 for package power): drawn in the chart of its group (<see cref="ChartGroupKey"/>, else its unit) as a 1 px dashed line in
    /// secondary ink with its name and value at the line; never a tile, never in a legend. Host API 0.1.3.
    /// </summary>
    public ChartRole ChartRole { get; init; } = ChartRole.Series;

    /// <summary>
    /// The value from which the reading is a warning (95 for a CPU that throttles at 100 °C), in the reading's unit. The tile meter carries a
    /// tick there, the tile says "High" from that value on, and its tooltip and accessible name say "warning at 95". Null: 90 for a
    /// temperature, none for the rest. Host API 0.1.3.
    /// </summary>
    public double? Warn { get; init; }

    /// <summary>
    /// A second figure on the reading's tile, small beside the number: a temperature with its fan (<c>fan 5,200 rpm</c>), a GPU with its two
    /// fans (<c>fans 4,350 · 4,340 rpm</c>). Shown while one of its metrics has a value. Null for none. Host API 0.1.3.
    /// </summary>
    public SecondaryReading? Secondary { get; init; }
}

/// <summary>How a <see cref="ReadingRow"/> takes part in its chart. Host API 0.1.3.</summary>
public enum ChartRole
{
    /// <summary>A reading drawn as data: a line, and a tile when the host picks it.</summary>
    Series,
    /// <summary>A limit or a target the data is read against: a dashed line labelled at the line, never a tile, never in a legend.</summary>
    Reference,
}

/// <summary>The second figure of a reading tile: a short word and one or two metrics, drawn small after the number. Host API 0.1.3.</summary>
/// <param name="LabelKey">Module-relative key of the short word before the figures ("fan", "fans"); empty for none.</param>
/// <param name="Metrics">One or two metrics, in the order shown (more are ignored). Their unit is written once, after the last.</param>
public sealed record SecondaryReading(string LabelKey, IReadOnlyList<MetricId> Metrics)
{
    /// <summary>A label and the metrics as arguments: <c>new SecondaryReading("tile.fans", gpuFan1, gpuFan2)</c>.</summary>
    /// <param name="labelKey">Module-relative key of the short word before the figures; empty for none.</param>
    /// <param name="metrics">One or two metrics.</param>
    public SecondaryReading(string labelKey, params MetricId[] metrics) : this(labelKey, (IReadOnlyList<MetricId>)metrics) { }
}

/// <summary>Whether a <see cref="ReadingRow"/> becomes a tile at the top of its module page.</summary>
public enum TileHint
{
    /// <summary>The host decides: up to four readings per page, the module's summary readings first, then by kind (temperature, power, load, speed).</summary>
    Auto,
    /// <summary>Make a tile of it (counts towards the four the host shows).</summary>
    Always,
    /// <summary>Never make a tile of it; the row still gets its sparkline.</summary>
    Never,
}

/// <summary>A row that folds related rows.</summary>
/// <param name="HeaderKey">Title key.</param>
/// <param name="Children">The folded rows.</param>
public sealed record ExpanderRow(string HeaderKey, IReadOnlyList<Row> Children) : Row(HeaderKey)
{
    /// <summary>Whether it starts open.</summary>
    public bool IsExpanded { get; init; }

    /// <summary>
    /// A control in the expander's own header, visible while it is closed: a tunnel's on switch, a profile's Apply button. A
    /// <see cref="ToggleRow"/>, <see cref="ChoiceRow"/>, <see cref="ButtonRow"/>, <see cref="ValueRow"/> or <see cref="ReadingRow"/>; its own
    /// header and description are not shown (the expander's are), its <see cref="Row.HardwareEffect"/>, approval and Revert work as in a group.
    /// With <see cref="Row.Line"/> on the expander the state of each item reads without opening it. Null for none. Host API 0.1.3.
    /// </summary>
    public Row? HeaderRow { get; init; }
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
