using System.Net;
using System.Text;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Single source of truth for the POC content region. Both the workspace seeding
/// (AgentTaskWorker) and the in-place editing tool (WorkspaceTools.SetPocContent)
/// go through here so the markers can never drift apart again — drift between the
/// prompt and the file was the original cause of the "poc-demo.html identical to
/// template" bug.
/// </summary>
public static class PocTemplate
{
    /// <summary>Workspace-relative path of the generated POC file.</summary>
    public const string MockupRelativePath = "03_Implementation/poc-demo.html";

    /// <summary>Must match the literal line in Prompts/Design/poc-template.html.</summary>
    public const string StartMarker = "<!-- POC_CONTENT_START : replace everything below with the feature UI -->";
    public const string EndMarker = "<!-- POC_CONTENT_END -->";

    /// <summary>Short, unique placeholder seeded into the content region.</summary>
    public const string Placeholder = "<!-- POC_CONTENT -->";

    /// <summary>
    /// Produces the poc-demo.html body from the template by collapsing everything
    /// between the markers into a single placeholder line. Returns null when the
    /// markers are missing/malformed so the caller can fall back to a raw copy.
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

    // ---- Shell customization (App Name, browser title, breadcrumb, left nav) ----------
    // These live OUTSIDE the POC_CONTENT markers, in the parts of the shell the dev agent
    // never used to touch — which is why generated POCs kept showing "App Name" and the
    // template's "Overview / Module A / Module B / Settings" menu. SetPocContent now edits
    // them here in the same single call. Each anchor is the literal markup from
    // poc-template.html that SeedFromTemplate copies verbatim; a missing anchor (or empty
    // input) leaves the document untouched, so a partial template can never throw or wipe
    // the shell — the worst case is the previous "left as template" behaviour.

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

    /// <summary>Sets the top-bar breadcrumb text.</summary>
    public static string ReplaceBreadcrumb(string current, string breadcrumb)
    {
        var text = WebUtility.HtmlEncode((breadcrumb ?? string.Empty).Trim());
        return text.Length == 0 ? current : ReplaceInner(current, BreadcrumbOpen, "</div>", text);
    }

    /// <summary>
    /// Rebuilds the left sidebar menu from <paramref name="items"/>, reusing the template's
    /// nav classes (nav-item / nav-group / nav-sub). The first entry is marked active and the
    /// first group is expanded, mirroring the template. Returns the input unchanged when there
    /// is nothing renderable or the nav element is missing.
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

    // Generic, deterministic icons so the menu always renders without the agent having to
    // supply SVGs: an outlined square for top-level items, a circle for sub-items, and the
    // template's chevron for expandable groups.
    private const string TopIcon = "<svg class=\"ico\" viewBox=\"0 0 24 24\"><rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"2\" /></svg>";
    private const string SubIcon = "<svg class=\"ico\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"9\" /></svg>";
    private const string Chevron = "<svg class=\"ico nav-chevron\" viewBox=\"0 0 24 24\"><path d=\"M6 9l6 6 6-6\" /></svg>";

    private static string RenderNav(IReadOnlyList<PocNavItem>? items)
    {
        if (items == null)
            return string.Empty;

        var sb = new StringBuilder();
        var activeUsed = false;
        var groupOpened = false;

        foreach (var item in items)
        {
            var label = WebUtility.HtmlEncode((item?.Label ?? string.Empty).Trim());
            if (label.Length == 0)
                continue;

            var active = activeUsed ? string.Empty : " active";
            activeUsed = true;

            var children = item!.Children?
                .Select(c => WebUtility.HtmlEncode((c ?? string.Empty).Trim()))
                .Where(c => c.Length > 0)
                .ToList() ?? new List<string>();

            if (children.Count == 0)
            {
                sb.Append("                    <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
                sb.Append("                        ").Append(TopIcon).Append('\n');
                sb.Append("                        <span class=\"nav-label\">").Append(label).Append("</span>\n");
                sb.Append("                    </div>\n");
                continue;
            }

            var open = groupOpened ? string.Empty : " open";
            groupOpened = true;

            sb.Append("                    <div class=\"nav-group").Append(open).Append("\">\n");
            sb.Append("                        <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
            sb.Append("                            ").Append(TopIcon).Append('\n');
            sb.Append("                            <span class=\"nav-label\">").Append(label).Append("</span>\n");
            sb.Append("                            ").Append(Chevron).Append('\n');
            sb.Append("                        </div>\n");
            sb.Append("                        <div class=\"nav-sub\">\n");
            foreach (var child in children)
            {
                sb.Append("                            <div class=\"nav-item\">").Append(SubIcon)
                  .Append("<span class=\"nav-label\">").Append(child).Append("</span></div>\n");
            }
            sb.Append("                        </div>\n");
            sb.Append("                    </div>\n");
        }

        return sb.ToString();
    }
}
