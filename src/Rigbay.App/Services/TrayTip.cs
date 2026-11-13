using System.Globalization;

namespace Rigbay.App.Services;

// The tray icon's tooltip, as a pure model: what the owner chose (TrayTipSettings), what the host knows right now (ITrayTipSource) and the
// text that comes out (TrayTipBuilder). No WinUI and no Rigbay.Core types, so the builder is tested with fakes.
// The file is compiled into the app and, as a linked source file, into Rigbay.Core.Tests. Keep it free of WinUI.

/// <summary>One item the owner put in a custom tooltip: a module's state line or one metric, with an optional label of their own.</summary>
sealed class TrayTipItem
{
    /// <summary><c>state:&lt;moduleId&gt;</c> for a module's state line, else a metric id (<c>cpu.fan</c>). Stable across restarts and module updates.</summary>
    public string Id { get; set; } = "";
    /// <summary>The module the item was added from (its id); lets an item of a module that is not installed any more be named. Empty for the host's own metrics.</summary>
    public string Module { get; set; } = "";
    /// <summary>The owner's short label; empty = the default short label (a state line has none by default).</summary>
    public string Label { get; set; } = "";
    /// <summary>Start a new line before this item (ignored for the first item that is shown).</summary>
    public bool NewLine { get; set; }
}

/// <summary>The setting "Tray tooltip": Automatic (the default) or Custom, with the items of the custom one.</summary>
sealed class TrayTipSettings
{
    public const string Automatic = "automatic", Custom = "custom";
    /// <summary>The most items a custom tooltip keeps (the 127 characters are used up long before; this only bounds a hand-edited file).</summary>
    public const int MaxItems = 24;
    /// <summary>The longest label kept.</summary>
    public const int MaxLabel = 24;

    /// <summary><c>automatic</c> or <c>custom</c>; a word that is neither is read as automatic.</summary>
    public string Mode { get; set; } = Automatic;
    /// <summary>Custom only: whether the first line is "Rigbay".</summary>
    public bool ShowTitle { get; set; } = true;
    /// <summary>Custom only: the items in order.</summary>
    public List<TrayTipItem> Items { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustom => string.Equals(Mode, Custom, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes a settings object read from a file safe to use: a known mode, no empty or repeated ids, clean labels, at most <see cref="MaxItems"/> items.</summary>
    public static TrayTipSettings Clean(TrayTipSettings? s)
    {
        var result = new TrayTipSettings { ShowTitle = s?.ShowTitle ?? true };
        result.Mode = s != null && s.IsCustom ? Custom : Automatic;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in s?.Items ?? new())
        {
            var id = (i?.Id ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;
            result.Items.Add(new TrayTipItem { Id = id, Module = (i!.Module ?? "").Trim(), Label = CleanLabel(i.Label), NewLine = i.NewLine });
            if (result.Items.Count == MaxItems) break;
        }
        return result;
    }

    /// <summary>A label on one line, single spaces, at most <see cref="MaxLabel"/> characters (cut between words when it can be).</summary>
    public static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var sb = new System.Text.StringBuilder();
        bool space = false;
        foreach (var c in label.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        var text = sb.ToString();
        return text.Length <= MaxLabel ? text : text[..MaxLabel].TrimEnd();
    }

    /// <summary>A copy with nothing shared with this object (the editor works on the live settings; a test compares against a copy).</summary>
    public TrayTipSettings Copy() => Clean(this);
}

/// <summary>Ids of the items.</summary>
static class TrayTipIds
{
    public const string StatePrefix = "state:";
    public static string State(string moduleId) => StatePrefix + moduleId;
    public static bool IsState(string id, out string moduleId)
    {
        bool yes = id.StartsWith(StatePrefix, StringComparison.Ordinal);
        moduleId = yes ? id[StatePrefix.Length..] : "";
        return yes;
    }
}

/// <summary>A reading of a module's summary (what the Automatic tooltip uses): <paramref name="Value"/> is the formatted number and unit, null when there is no reading.</summary>
sealed record TipReading(string Id, string Label, string? Value);

/// <summary>A module as the tooltip sees it. <paramref name="State"/> is the resolved state line (null or empty = none).</summary>
sealed record TipModule(string Id, string Name, bool Running, string? State, IReadOnlyList<TipReading> Summary);

/// <summary>
/// A metric on the bus as the tooltip sees it. <paramref name="Value"/> is the formatted reading, null when there is none (never published or withdrawn);
/// <paramref name="Fresh"/> false when the last sample is older than the metric's own rhythm allows; <paramref name="OwnerRunning"/> false when the module that
/// publishes it is not running.
/// </summary>
sealed record TipMetric(string Id, string Label, string OwnerId, bool OwnerRunning, string? Value, bool Fresh);

/// <summary>What the builder asks the host. The app's implementation reads <c>RigbayHost</c>; the tests use a fake.</summary>
interface ITrayTipSource
{
    /// <summary>Every module the host knows, in rack order.</summary>
    IReadOnlyList<TipModule> Modules { get; }
    /// <summary>A metric by id, or null when the bus never heard of it.</summary>
    TipMetric? Metric(string id);
}

/// <summary>Why an item of a custom tooltip is, or is not, in the text.</summary>
enum TipItemStatus
{
    /// <summary>In the text.</summary>
    Shown,
    /// <summary>Left out because the text would be longer than the shell takes.</summary>
    TooLong,
    /// <summary>Its module is not running.</summary>
    ModuleOff,
    /// <summary>No reading (or no state line) right now.</summary>
    NoReading,
    /// <summary>The last reading is older than the metric's rhythm allows.</summary>
    Stale,
    /// <summary>Nothing by this id is known: the module is not installed (any more).</summary>
    Missing,
}

/// <summary>One item of a custom tooltip with what became of it. <paramref name="Text"/> is what it contributes when shown (or would, for a too long one); null when it has nothing.</summary>
sealed record TipItemResult(TrayTipItem Item, TipItemStatus Status, string? Text, string Title, string Group);

/// <summary>The tooltip text and, for Custom, what became of each item.</summary>
sealed record TipResult(string Text, IReadOnlyList<TipItemResult> Items)
{
    /// <summary>How many items were left out for length.</summary>
    public int DroppedForLength => Items.Count(i => i.Status == TipItemStatus.TooLong);
}

static class TrayTipBuilder
{
    /// <summary>The longest tooltip the shell takes (<c>szTip</c> is 128 characters with the terminating zero).</summary>
    public const int MaxTip = 127;
    public const string Title = "Rigbay";
    public const string Join = " · ";

    public static TipResult Build(TrayTipSettings settings, ITrayTipSource source) =>
        settings.IsCustom ? BuildCustom(settings, source) : new TipResult(BuildAutomatic(source), Array.Empty<TipItemResult>());

    /// <summary>
    /// "Rigbay", then the first two running modules that have a state line or a reading: "Balanced · CPU 82 °C", "Fans Auto · CPU fan 3,448 rpm".
    /// A figure already said on an earlier line is not repeated. At most <see cref="MaxTip"/> characters (a longer text is cut with an ellipsis: this is
    /// the behaviour the tooltip had before it could be customised, kept as it was).
    /// </summary>
    public static string BuildAutomatic(ITrayTipSource source)
    {
        var lines = new List<string> { Title };
        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in source.Modules)
        {
            if (!m.Running) continue;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.State)) parts.Add(m.State);
            foreach (var r in m.Summary)
            {
                if (!said.Add(r.Id) || r.Value == null) continue;
                parts.Add(r.Label + " " + r.Value);
            }
            if (parts.Count == 0) continue;
            lines.Add(string.Join(Join, parts));
            if (lines.Count == 3) break;
        }
        var tip = string.Join("\n", lines);
        return tip.Length <= MaxTip ? tip : tip[..(MaxTip - 1)] + "…";
    }

    static TipItemResult Resolve(TrayTipItem item, ITrayTipSource source)
    {
        var label = TrayTipSettings.CleanLabel(item.Label);
        var modules = source.Modules;
        if (TrayTipIds.IsState(item.Id, out var moduleId))
        {
            var m = modules.FirstOrDefault(x => x.Id == moduleId);
            if (m == null) return new(item, TipItemStatus.Missing, null, item.Id, item.Module.Length > 0 ? item.Module : moduleId);
            string? text = string.IsNullOrEmpty(m.State) ? null : (label.Length > 0 ? label + " " : "") + m.State;
            var status = !m.Running ? TipItemStatus.ModuleOff : text == null ? TipItemStatus.NoReading : TipItemStatus.Shown;
            return new(item, status, status == TipItemStatus.Shown ? text : null, label.Length > 0 ? label : m.State ?? "", m.Name);
        }
        var metric = source.Metric(item.Id);
        if (metric == null)
        {
            var home = item.Module.Length > 0 ? modules.FirstOrDefault(x => x.Id == item.Module) : null;
            if (home == null && item.Module.Length > 0) return new(item, TipItemStatus.Missing, null, item.Id, item.Module);
            return new(item, home is { Running: false } ? TipItemStatus.ModuleOff : TipItemStatus.NoReading, null, item.Id, home?.Name ?? item.Module);
        }
        var shownLabel = label.Length > 0 ? label : metric.Label;
        var group = modules.FirstOrDefault(x => x.Id == metric.OwnerId)?.Name ?? (metric.OwnerId == "host" ? Title : metric.OwnerId);
        TipItemStatus st = !metric.OwnerRunning ? TipItemStatus.ModuleOff : metric.Value == null ? TipItemStatus.NoReading : !metric.Fresh ? TipItemStatus.Stale : TipItemStatus.Shown;
        return new(item, st, st == TipItemStatus.Shown ? shownLabel + " " + metric.Value : null, shownLabel, group);
    }

    /// <summary>
    /// The custom tooltip: the items that have something to say, in order, joined with " · " inside a line; an item with <see cref="TrayTipItem.NewLine"/> starts
    /// a line (when that item itself has nothing to say, the break moves on to the next one that does, so the items behind it do not slide up onto the line before).
    /// Too long for the shell: whole items are dropped from the end until it fits; nothing is cut in the middle of a number.
    /// </summary>
    static TipResult BuildCustom(TrayTipSettings settings, ITrayTipSource source)
    {
        var results = settings.Items.Select(i => Resolve(i, source)).ToList();
        var shown = Enumerable.Range(0, results.Count).Where(i => results[i].Status == TipItemStatus.Shown).ToList();

        string Assemble()
        {
            var sb = new System.Text.StringBuilder();
            if (settings.ShowTitle) sb.Append(Title);
            bool pendingBreak = false, first = true;
            int next = 0;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                bool isShown = next < shown.Count && shown[next] == i;
                if (r.Item.NewLine) pendingBreak = true;
                if (!isShown) continue;
                next++;
                if (first) { if (settings.ShowTitle) sb.Append('\n'); }   // the first item starts under the title, or is the text itself
                else sb.Append(pendingBreak ? "\n" : Join);
                first = false; pendingBreak = false;
                sb.Append(r.Text);
            }
            return sb.ToString();
        }

        var text = Assemble();
        while (text.Length > MaxTip && shown.Count > 0)
        {
            int last = shown[^1];
            results[last] = results[last] with { Status = TipItemStatus.TooLong };
            shown.RemoveAt(shown.Count - 1);
            text = Assemble();
        }
        if (text.Length == 0) text = Title;   // nothing to say and no title: an empty tooltip would remove the tooltip, so the name stays
        return new TipResult(text, results);
    }

    /// <summary>The "n / 127" the settings page shows. Counts the way the shell does: every character once (a new line is one).</summary>
    public static string Counter(string text) => string.Format(CultureInfo.InvariantCulture, "{0} / {1}", text.Length, MaxTip);
}
