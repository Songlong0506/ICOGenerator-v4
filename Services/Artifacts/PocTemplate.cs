using System.Net;
using System.Text;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Single source of truth for the POC content region; both workspace seeding (AgentTaskWorker)
/// and in-place editing (WorkspaceTools.SetPocContent) go through here so the markers can't drift
/// apart — that drift was the original cause of the "poc-demo.html identical to template" bug.
/// </summary>
public static class PocTemplate
{
    public const string MockupRelativePath = "04_Implementation/poc-demo.html";

    /// <summary>Must match the literal line in Prompts/Design/poc-template.html.</summary>
    public const string StartMarker = "<!-- POC_CONTENT_START : replace everything below with the feature UI -->";
    public const string EndMarker = "<!-- POC_CONTENT_END -->";

    public const string Placeholder = "<!-- POC_CONTENT -->";

    /// <summary>
    /// Builds the poc-demo.html body by collapsing everything between the markers into a single
    /// placeholder line. Returns null when the markers are missing/malformed (caller falls back to a raw copy).
    /// </summary>
    public static string? SeedFromTemplate(string template)
    {
        if (!TryLocateRegion(template, out var afterStart, out var endIdx))
            return null;

        return template[..afterStart]
            + "\n                    " + Placeholder + "\n                    "
            + template[endIdx..];
    }

    /// <summary>
    /// Replaces everything between the markers (exclusive) with <paramref name="newContent"/>,
    /// keeping the markers intact. Returns null when the markers are missing/malformed.
    /// </summary>
    public static string? ReplaceContent(string current, string newContent)
    {
        if (!TryLocateRegion(current, out var afterStart, out var endIdx))
            return null;

        return current[..afterStart]
            + "\n" + newContent.Trim('\n') + "\n                    "
            + current[endIdx..];
    }

    private static bool TryLocateRegion(string content, out int afterStart, out int endIdx)
    {
        afterStart = 0;
        var startIdx = content.IndexOf(StartMarker, StringComparison.Ordinal);
        endIdx = content.IndexOf(EndMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx <= startIdx)
            return false;

        afterStart = startIdx + StartMarker.Length;
        return true;
    }

    // Shell customization (App Name, browser title, breadcrumb, left nav) — these live OUTSIDE the
    // POC_CONTENT markers, the parts the dev agent never touched (why POCs kept showing "App Name"
    // and the template menu). Anchors are literal markup from poc-template.html; a missing anchor or
    // empty input leaves the document untouched, so a partial template can't throw or wipe the shell.

    private const string AppNameOpen = "<span class=\"app-name\">";
    private const string TitleOpen = "<title>";
    private const string BreadcrumbOpen = "<div class=\"breadcrumb\">";
    private const string NavOpen = "<nav class=\"sidebar-nav\">";
    private const string NavClose = "</nav>";

    /// <summary>Sets the sidebar header name and the browser tab title.</summary>
    public static string ReplaceAppName(string current, string appName)
    {
        var name = WebUtility.HtmlEncode((appName ?? string.Empty).Trim());
        if (name.Length == 0)
            return current;

        current = ReplaceInner(current, AppNameOpen, "</span>", name);
        current = ReplaceInner(current, TitleOpen, "</title>", name);
        return current;
    }

    public static string ReplaceBreadcrumb(string current, string breadcrumb)
    {
        var text = WebUtility.HtmlEncode((breadcrumb ?? string.Empty).Trim());
        return text.Length == 0 ? current : ReplaceInner(current, BreadcrumbOpen, "</div>", text);
    }

    /// <summary>
    /// Rebuilds the left sidebar menu from <paramref name="items"/> using the template's nav classes;
    /// the first entry is active and the first group expanded. Returns input unchanged when there's
    /// nothing renderable or the nav element is missing.
    /// </summary>
    public static string ReplaceNav(string current, IReadOnlyList<PocNavItem> items)
    {
        var rendered = RenderNav(items);
        if (rendered.Length == 0)
            return current;

        var startIdx = current.IndexOf(NavOpen, StringComparison.Ordinal);
        if (startIdx < 0)
            return current;

        var innerStart = startIdx + NavOpen.Length;
        var closeIdx = current.IndexOf(NavClose, innerStart, StringComparison.Ordinal);
        if (closeIdx < 0)
            return current;

        return current[..innerStart] + "\n" + rendered + "                " + current[closeIdx..];
    }

    // Replaces the text between the first `open` tag and the next `close` after it.
    private static string ReplaceInner(string content, string open, string close, string newInner)
    {
        var openIdx = content.IndexOf(open, StringComparison.Ordinal);
        if (openIdx < 0)
            return content;

        var innerStart = openIdx + open.Length;
        var closeIdx = content.IndexOf(close, innerStart, StringComparison.Ordinal);
        if (closeIdx < 0)
            return content;

        return content[..innerStart] + newInner + content[closeIdx..];
    }

    // Sidebar icons come from Bootstrap Icons, which the shell loads once via a <link> in <head>, so
    // the menu can use any of the ~2000 icons without hand-defining SVGs. Each item renders an
    // <i class="bi bi-NAME"> where NAME is the agent-supplied PocNavItem.Icon, falling back to
    // DefaultIcon when an item doesn't specify one. The chevron marking an expandable group stays an
    // inline SVG (its rotate animation is tied to .nav-chevron).
    private const string Chevron = "<svg class=\"ico nav-chevron\" viewBox=\"0 0 24 24\"><path d=\"M6 9l6 6 6-6\" /></svg>";

    private const string DefaultIcon = "dot";

    private static string RenderNav(IReadOnlyList<PocNavItem>? items)
    {
        if (items == null)
            return string.Empty;

        var sb = new StringBuilder();
        var activeUsed = false;
        var groupOpened = false;

        foreach (var item in items)
        {
            var rawLabel = (item?.Label ?? string.Empty).Trim();
            if (rawLabel.Length == 0)
                continue;

            var label = WebUtility.HtmlEncode(rawLabel);
            var icon = IconMarkup(item!.Icon);

            var active = activeUsed ? string.Empty : " active";
            activeUsed = true;

            var children = item.Children?
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Label))
                .ToList() ?? new List<PocNavItem>();

            if (children.Count == 0)
            {
                sb.Append("                    <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
                sb.Append("                        ").Append(icon).Append('\n');
                sb.Append("                        <span class=\"nav-label\">").Append(label).Append("</span>\n");
                sb.Append("                    </div>\n");
                continue;
            }

            var open = groupOpened ? string.Empty : " open";
            groupOpened = true;

            sb.Append("                    <div class=\"nav-group").Append(open).Append("\">\n");
            sb.Append("                        <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
            sb.Append("                            ").Append(icon).Append('\n');
            sb.Append("                            <span class=\"nav-label\">").Append(label).Append("</span>\n");
            sb.Append("                            ").Append(Chevron).Append('\n');
            sb.Append("                        </div>\n");
            sb.Append("                        <div class=\"nav-sub\">\n");
            foreach (var child in children)
            {
                var childLabel = child.Label.Trim();
                sb.Append("                            <div class=\"nav-item\">").Append(IconMarkup(child.Icon))
                  .Append("<span class=\"nav-label\">").Append(WebUtility.HtmlEncode(childLabel)).Append("</span></div>\n");
            }
            sb.Append("                        </div>\n");
            sb.Append("                    </div>\n");
        }

        return sb.ToString();
    }

    // Renders the Bootstrap Icons element for a nav item: the agent-supplied name (sanitized so it
    // can't break out of the class attribute) or DefaultIcon when none/invalid is given.
    private static string IconMarkup(string? icon) =>
        "<i class=\"bi bi-" + (SanitizeIconName(icon) ?? DefaultIcon) + "\" aria-hidden=\"true\"></i>";

    // Bootstrap Icons names are [a-z0-9-]; lower-case, drop an optional leading "bi-"/"bi ", and keep
    // only that safe charset so an agent-supplied value can never break out of the class attribute.
    // Returns null when nothing usable remains, so the caller falls back to DefaultIcon.
    private static string? SanitizeIconName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("bi-", StringComparison.Ordinal) || s.StartsWith("bi ", StringComparison.Ordinal))
            s = s[3..];

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? null : cleaned;
    }
}
