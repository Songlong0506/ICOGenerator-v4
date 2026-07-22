using System.Text.Json;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// One entry in the generated POC's left sidebar menu: a leaf when <see cref="Children"/> is null/empty, else an expandable group.
/// Populated by the Developer agent (via SetPocContent) so the nav reflects the real feature, not the template's placeholder menu.
/// </summary>
public sealed class PocNavItem
{
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Optional Bootstrap Icons name (e.g. "house", "cart3", "people"; the leading "bi-" is optional).
    /// When omitted, the renderer infers an icon from the label; the agent can name any icon from the
    /// bundled Bootstrap Icons set so the menu isn't limited to a hand-maintained list.
    /// </summary>
    public string? Icon { get; set; }

    public List<PocNavItem>? Children { get; set; }

    /// <summary>
    /// Tolerant parser for the agent-supplied 'navItems'. Malformed entries are skipped rather than thrown, so a nav slip never blocks the rest of the POC update. Returns an empty list for non-arrays.
    /// </summary>
    public static List<PocNavItem> ParseList(JsonElement element)
    {
        // Models sometimes DOUBLE-ENCODE the array: navItems arrives as the string "[{\"label\":…}]"
        // instead of a JSON array. Unwrap it here — dropping it silently left generated POCs with all
        // their page-view sections but no matching sidebar tabs to open them.
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith('['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    return ParseList(doc.RootElement);
                }
                catch (JsonException)
                {
                    // not valid JSON after all — fall through to the empty result below
                }
            }
        }

        var result = new List<PocNavItem>();
        if (element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in element.EnumerateArray())
        {
            var item = ParseEntry(entry);
            if (item == null)
                continue;

            if (entry.ValueKind == JsonValueKind.Object
                && TryGetProp(entry, "children", out var childrenEl)
                && childrenEl.ValueKind == JsonValueKind.Array)
            {
                var children = new List<PocNavItem>();
                foreach (var childEl in childrenEl.EnumerateArray())
                {
                    var child = ParseEntry(childEl);
                    if (child != null)
                    {
                        child.Children = null; // the sidebar supports a single nesting level
                        children.Add(child);
                    }
                }

                if (children.Count > 0)
                    item.Children = children;
            }

            result.Add(item);
        }

        return result;
    }

    // Parses a single entry (a bare label string, or an object with label + optional icon) into a
    // leaf item. Returns null when there's no usable label, so blanks/non-objects are skipped.
    private static PocNavItem? ParseEntry(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            var leaf = entry.GetString();
            return string.IsNullOrWhiteSpace(leaf) ? null : new PocNavItem { Label = leaf.Trim() };
        }

        if (entry.ValueKind != JsonValueKind.Object)
            return null;

        var label = GetLabel(entry);
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var item = new PocNavItem { Label = label.Trim() };

        var icon = GetStringProp(entry, "icon");
        if (!string.IsNullOrWhiteSpace(icon))
            item.Icon = icon.Trim();

        return item;
    }

    // "label" is the documented key; "title"/"name" are tolerated aliases a model might emit.
    private static string? GetLabel(JsonElement obj)
        => GetStringProp(obj, "label") ?? GetStringProp(obj, "title") ?? GetStringProp(obj, "name");

    private static string? GetStringProp(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // Case-insensitive property lookup (JsonElement.TryGetProperty is case-sensitive).
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
