using System.Text.Json;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// One entry in the generated POC's left sidebar menu. A leaf when <see cref="Children"/>
/// is null/empty; otherwise an expandable group whose sub-items are the child labels.
/// Populated by the Developer agent (via the SetPocContent tool) so the POC navigation
/// reflects the real feature instead of the template's placeholder menu.
/// </summary>
public sealed class PocNavItem
{
    public string Label { get; set; } = string.Empty;

    public List<string>? Children { get; set; }

    /// <summary>
    /// Tolerant parser for the agent-supplied 'navItems' argument. Accepts an array of
    /// strings (leaves) and/or objects ({ label/title/name, children }); 'children' may be
    /// strings or objects. Anything malformed is skipped rather than throwing, so a nav
    /// formatting slip never blocks the rest of the POC update (App Name, breadcrumb, content).
    /// Returns an empty list for non-arrays.
    /// </summary>
    public static List<PocNavItem> ParseList(JsonElement element)
    {
        var result = new List<PocNavItem>();
        if (element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var leaf = entry.GetString();
                if (!string.IsNullOrWhiteSpace(leaf))
                    result.Add(new PocNavItem { Label = leaf.Trim() });
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var label = GetLabel(entry);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var item = new PocNavItem { Label = label.Trim() };

            if (TryGetProp(entry, "children", out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
            {
                var children = new List<string>();
                foreach (var childEl in childrenEl.EnumerateArray())
                {
                    var child = childEl.ValueKind == JsonValueKind.String ? childEl.GetString() : GetLabel(childEl);
                    if (!string.IsNullOrWhiteSpace(child))
                        children.Add(child.Trim());
                }

                if (children.Count > 0)
                    item.Children = children;
            }

            result.Add(item);
        }

        return result;
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
