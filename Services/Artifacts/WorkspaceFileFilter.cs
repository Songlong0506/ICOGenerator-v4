namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Single source of truth for "regenerable" directories — dependency installs, build output and VCS
/// metadata (node_modules, bin, obj, .git, .vs) — that the app must never surface as project artifacts.
/// Shared by everything that walks a workspace tree (source packaging, the agent dashboard's document
/// loader, and the agent's own ListFiles/SearchFiles tools) so a built project's tens of thousands of
/// node_modules/bin/obj files are never enumerated, read into memory, or shown.
/// </summary>
public static class WorkspaceFileFilter
{
    // Matched per path segment, so nested copies (e.g. a sub-package's own node_modules) are skipped too.
    public static readonly IReadOnlySet<string> RegenerableDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs"
    };

    /// <summary>
    /// True when <paramref name="fullPath"/> sits under a regenerable directory anywhere between
    /// <paramref name="rootPath"/> and the file itself.
    /// </summary>
    public static bool IsInRegenerableDirectory(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Skip the last segment: it's the file name, not a directory.
        for (var i = 0; i < segments.Length - 1; i++)
            if (RegenerableDirectories.Contains(segments[i]))
                return true;

        return false;
    }
}
