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

    /// <summary>
    /// Shell-chrome regions the generator may also rebrand so the sidebar/topbar match
    /// the feature instead of staying on the template defaults ("App Name", "Module A"…).
    /// All must match the literal markers in Prompts/Design/poc-template.html.
    /// </summary>
    public const string AppNameStartMarker = "<!-- POC_APPNAME_START -->";
    public const string AppNameEndMarker = "<!-- POC_APPNAME_END -->";
    public const string NavStartMarker = "<!-- POC_NAV_START : replace the nav items/groups below to match the feature -->";
    public const string NavEndMarker = "<!-- POC_NAV_END -->";
    public const string BreadcrumbStartMarker = "<!-- POC_BREADCRUMB_START -->";
    public const string BreadcrumbEndMarker = "<!-- POC_BREADCRUMB_END -->";

    /// <summary>Short, unique placeholder seeded into the content region.</summary>
    public const string Placeholder = "<!-- POC_CONTENT -->";

    /// <summary>
    /// Produces the poc-demo.html body from the template by collapsing everything
    /// between the markers into a single placeholder line. Returns null when the
    /// markers are missing/malformed so the caller can fall back to a raw copy.
    /// </summary>
    public static string? SeedFromTemplate(string template)
    {
        if (!TryLocateRegion(template, StartMarker, EndMarker, out var afterStart, out var endIdx))
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
        return ReplaceRegion(current, StartMarker, EndMarker,
            "\n" + newContent.Trim('\n') + "\n                    ");
    }

    /// <summary>Rebrands the sidebar App Name. Returns null when the markers are missing.</summary>
    public static string? ReplaceAppName(string current, string appName)
    {
        return ReplaceRegion(current, AppNameStartMarker, AppNameEndMarker, appName.Trim());
    }

    /// <summary>Replaces the sidebar navigation items/groups. Returns null when the markers are missing.</summary>
    public static string? ReplaceNav(string current, string navHtml)
    {
        return ReplaceRegion(current, NavStartMarker, NavEndMarker,
            "\n                    " + navHtml.Trim('\n') + "\n                    ");
    }

    /// <summary>Replaces the top-bar breadcrumb. Returns null when the markers are missing.</summary>
    public static string? ReplaceBreadcrumb(string current, string breadcrumb)
    {
        return ReplaceRegion(current, BreadcrumbStartMarker, BreadcrumbEndMarker, breadcrumb.Trim());
    }

    private static string? ReplaceRegion(string current, string startMarker, string endMarker, string inner)
    {
        if (!TryLocateRegion(current, startMarker, endMarker, out var afterStart, out var endIdx))
            return null;

        return current[..afterStart] + inner + current[endIdx..];
    }

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
}
