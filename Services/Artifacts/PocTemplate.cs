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
    /// Second marked region: the POC page-logic script (business-rule behaviour — computed values,
    /// sign/state flows, role simulation), written only via SetPocScript/AppendPocScript. Lives AFTER
    /// the shell script so the shell hooks (window.pocToast / window.pocNavigate) already exist when
    /// it runs. Must match the literal lines in Prompts/Design/poc-template.html.
    /// </summary>
    public const string ScriptStartMarker = "<!-- POC_SCRIPT_START : page logic injected via SetPocScript -->";
    public const string ScriptEndMarker = "<!-- POC_SCRIPT_END -->";

    public const string ScriptPlaceholder = "/* POC_SCRIPT */";

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
    /// Removes the leading developer-agent instruction block — the big comment between the DOCTYPE and the
    /// opening &lt;html&gt; tag in poc-template.html. That block is guidance for the LLM authoring the POC and
    /// must never reach the browser: it carries literal &lt;script&gt; text and the marker names, so if the
    /// comment is ever disturbed (a re-emit, an encoding slip, a stray early "--&gt;") its instructions leak
    /// onto the page as raw text — the "(POC_SCRIPT_START/END) holds ONE …" garbage instead of the POC.
    /// It strips by anchors (keep the DOCTYPE, drop everything up to &lt;html&gt;), NOT by matching the comment,
    /// so it works even when the comment itself is malformed. Idempotent, and returns the input unchanged
    /// when there is no &lt;html&gt; tag to anchor on (or the document already starts with it).
    /// </summary>
    public static string StripDeveloperGuide(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        var htmlIdx = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlIdx <= 0)
            return html; // no <html>, or it's already the first tag — nothing before it to strip

        // Keep a DOCTYPE that sits before <html> (so the page never falls into quirks mode), and drop
        // only the block between it and <html>.
        var doctypeIdx = html.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        if (doctypeIdx >= 0 && doctypeIdx < htmlIdx)
        {
            var doctypeEnd = html.IndexOf('>', doctypeIdx);
            if (doctypeEnd >= 0 && doctypeEnd < htmlIdx)
                return html[..(doctypeEnd + 1)] + "\n" + html[htmlIdx..];
        }

        // No usable DOCTYPE before <html>: drop whatever precedes <html> outright.
        return html[htmlIdx..];
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

    /// <summary>
    /// Appends <paramref name="addition"/> to the END of the content region (just before the end
    /// marker), keeping any content already there and the markers intact. This lets a large POC be
    /// built across several tool calls so no single call has to carry the whole page — the call that
    /// would be cut off by the token limit (finish_reason=length). The seed placeholder is dropped the
    /// first time real content is appended. Returns the input unchanged when <paramref name="addition"/>
    /// is blank, or null when the markers are missing/malformed.
    /// </summary>
    public static string? AppendContent(string current, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
            return current;
        if (!TryLocateRegion(current, out var afterStart, out var endIdx))
            return null;

        // Content already between the markers; drop the seed placeholder so the first append replaces
        // the invisible <!-- POC_CONTENT --> comment instead of stacking after it. TrimEnd collapses the
        // trailing indentation so the new chunk lines up the same way ReplaceContent lays out the first one.
        var existing = current[afterStart..endIdx].Replace(Placeholder, string.Empty).TrimEnd();

        return current[..afterStart]
            + existing
            + "\n" + addition.Trim('\n') + "\n                    "
            + current[endIdx..];
    }

    private static bool TryLocateRegion(string content, out int afterStart, out int endIdx) =>
        TryLocateRegion(content, StartMarker, EndMarker, out afterStart, out endIdx);

    private static bool TryLocateRegion(string content, string startMarker, string endMarker, out int afterStart, out int endIdx)
    {
        afterStart = 0;
        var startIdx = content.IndexOf(startMarker, StringComparison.Ordinal);
        endIdx = content.IndexOf(endMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx <= startIdx)
            return false;

        afterStart = startIdx + startMarker.Length;
        return true;
    }

    // ---- POC page-logic script (the POC_SCRIPT region) ----

    /// <summary>
    /// Replaces the POC_SCRIPT region with a single &lt;script&gt; carrying <paramref name="script"/>
    /// (normalized: an accidental &lt;script&gt; wrapper or markdown fence is stripped, "&lt;/script"
    /// inside the code is escaped so it can't terminate the element early). When the file predates the
    /// script region (a workspace seeded from an older template), the whole region is grafted in just
    /// before &lt;/body&gt; so SetPocScript keeps working without re-seeding the demo. Returns the input
    /// unchanged when the script is blank, or null when there is neither a region nor a &lt;/body&gt;.
    /// </summary>
    public static string? ReplaceScript(string current, string script)
    {
        var js = NormalizeScript(script);
        if (js.Length == 0)
            return current;

        if (TryLocateRegion(current, ScriptStartMarker, ScriptEndMarker, out var afterStart, out var endIdx))
            return current[..afterStart] + ScriptBlock(js) + current[endIdx..];

        var bodyIdx = current.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyIdx < 0)
            return null;

        return current[..bodyIdx]
            + "    " + ScriptStartMarker + ScriptBlock(js) + ScriptEndMarker + "\n"
            + current[bodyIdx..];
    }

    /// <summary>
    /// Appends <paramref name="addition"/> to the END of the script already in the POC_SCRIPT region,
    /// so long page logic can be delivered across several small calls (same reason AppendContent
    /// exists: one big call gets cut off at the token limit). Chunks share one &lt;script&gt; element
    /// and run in order. On an empty region (or a file without one) this behaves like ReplaceScript.
    /// </summary>
    public static string? AppendScript(string current, string addition)
    {
        var js = NormalizeScript(addition);
        if (js.Length == 0)
            return current;

        if (!TryLocateRegion(current, ScriptStartMarker, ScriptEndMarker, out var afterStart, out var endIdx))
            return ReplaceScript(current, addition); // no region yet: also covers the pre-region fallback

        var existing = ExtractScriptBody(current[afterStart..endIdx]);
        var merged = existing.Length == 0 ? js : existing + "\n\n" + js;
        return current[..afterStart] + ScriptBlock(merged) + current[endIdx..];
    }

    /// <summary>
    /// The JavaScript currently in the POC_SCRIPT region ("" when the region is missing or still the
    /// seed placeholder). Used by the audit to tell a behaving POC from static screens.
    /// </summary>
    public static string GetScriptBody(string current) =>
        TryLocateRegion(current, ScriptStartMarker, ScriptEndMarker, out var afterStart, out var endIdx)
            ? ExtractScriptBody(current[afterStart..endIdx])
            : string.Empty;

    private static string ScriptBlock(string js) =>
        "\n    <script>\n" + js + "\n    </script>\n    ";

    private static string ExtractScriptBody(string region)
    {
        var open = region.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
            return string.Empty;
        var openEnd = region.IndexOf('>', open);
        var close = region.LastIndexOf("</script>", StringComparison.OrdinalIgnoreCase);
        if (openEnd < 0 || close <= openEnd)
            return string.Empty;
        return region[(openEnd + 1)..close].Replace(ScriptPlaceholder, string.Empty).Trim();
    }

    // Models sometimes wrap the JS in a <script> tag or a markdown fence despite instructions — accept
    // it and keep just the code. "</script" inside the code would terminate the inline element early
    // (the classic breakout), so it's escaped the standard way; legitimate JS only carries that text
    // inside a string literal, where "<\/script" is equivalent.
    private static string NormalizeScript(string? script)
    {
        var js = (script ?? string.Empty).Trim();

        if (js.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = js.IndexOf('\n');
            js = firstLineEnd < 0 ? string.Empty : js[(firstLineEnd + 1)..];
            var closingFence = js.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
                js = js[..closingFence];
            js = js.Trim();
        }

        if (js.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
        {
            var openEnd = js.IndexOf('>');
            var close = js.LastIndexOf("</script>", StringComparison.OrdinalIgnoreCase);
            if (openEnd >= 0 && close > openEnd)
                js = js[(openEnd + 1)..close].Trim();
        }

        return js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
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
