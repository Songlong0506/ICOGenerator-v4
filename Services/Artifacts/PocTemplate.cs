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
}
