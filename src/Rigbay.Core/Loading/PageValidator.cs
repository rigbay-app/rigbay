using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>Structural checks of a <see cref="PageModel"/> so the UI never meets a null or an impossible row.</summary>
public static class PageValidator
{
    public static IReadOnlyList<string> Validate(PageModel page)
    {
        var problems = new List<string>();
        if (page.Groups is null) { problems.Add("Groups is null"); return problems; }

        int g = 0;
        foreach (var group in page.Groups)
        {
            g++;
            if (group is null) { problems.Add($"group {g} is null"); continue; }
            if (string.IsNullOrWhiteSpace(group.HeaderKey)) problems.Add($"group {g} has no HeaderKey");
            if (group.Rows is null) { problems.Add($"group '{group.HeaderKey}' has no Rows"); continue; }
            foreach (var row in group.Rows) CheckRow(row, group.HeaderKey, problems, depth: 0);
        }

        if (page.Links is not null)
            foreach (var link in page.Links)
                if (link is null || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    problems.Add($"link '{link?.TextKey}' must have an http or https address");
        return problems;
    }

    private static void CheckRow(Row? row, string where, List<string> problems, int depth)
    {
        if (row is null) { problems.Add($"a null row in '{where}'"); return; }
        if (depth > 4) { problems.Add($"expanders nested too deeply in '{where}'"); return; }
        if (row is not CustomRow && string.IsNullOrWhiteSpace(row.HeaderKey)) problems.Add($"a {row.GetType().Name} in '{where}' has no HeaderKey");
        if (row.Enabled is null) problems.Add($"{row.GetType().Name} '{row.HeaderKey}': Enabled is null");
        if (row.HardwareEffect is { } he && string.IsNullOrWhiteSpace(he.DescriptionKey)) problems.Add($"{row.GetType().Name} '{row.HeaderKey}': HardwareEffect has no DescriptionKey");

        switch (row)
        {
            case ToggleRow r when r.Value is null: problems.Add($"ToggleRow '{r.HeaderKey}': Value is null"); break;
            case ChoiceRow r:
                if (r.Value is null) problems.Add($"ChoiceRow '{r.HeaderKey}': Value is null");
                if (r.Options is null || r.Options.Count == 0) problems.Add($"ChoiceRow '{r.HeaderKey}': no options");
                else if (r.Options.Select(o => o.Id).Distinct().Count() != r.Options.Count) problems.Add($"ChoiceRow '{r.HeaderKey}': duplicate option ids");
                break;
            case NumberRow r:
                if (r.Value is null) problems.Add($"NumberRow '{r.HeaderKey}': Value is null");
                if (!(r.Minimum <= r.Maximum)) problems.Add($"NumberRow '{r.HeaderKey}': Minimum is above Maximum");
                if (!(r.Step > 0)) problems.Add($"NumberRow '{r.HeaderKey}': Step must be above 0");
                break;
            case TextRow r when r.Value is null: problems.Add($"TextRow '{r.HeaderKey}': Value is null"); break;
            case ShortcutRow r when r.Value is null: problems.Add($"ShortcutRow '{r.HeaderKey}': Value is null"); break;
            case ButtonRow r when r.InvokeAsync is null: problems.Add($"ButtonRow '{r.HeaderKey}': InvokeAsync is null"); break;
            case ValueRow r when r.Value is null: problems.Add($"ValueRow '{r.HeaderKey}': Value is null"); break;
            case ReadingRow r when r.Metric.Value.Length == 0: problems.Add($"ReadingRow '{r.HeaderKey}': no metric"); break;
            case ReadingRow r when r.RangeMin is { } lo && r.RangeMax is { } hi && !(lo < hi && double.IsFinite(lo) && double.IsFinite(hi)): problems.Add($"ReadingRow '{r.HeaderKey}': RangeMin must be below RangeMax"); break;
            case CustomRow r when string.IsNullOrWhiteSpace(r.ControlId): problems.Add("a CustomRow has no ControlId"); break;
            case ExpanderRow r:
                if (r.Children is null) problems.Add($"ExpanderRow '{r.HeaderKey}': Children is null");
                else foreach (var child in r.Children) CheckRow(child, r.HeaderKey, problems, depth + 1);
                break;
        }
    }
}
