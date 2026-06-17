namespace ICOGenerator.Services.Artifacts;

public class WorkspacePathResolver
{
    private readonly IConfiguration _configuration;

    public WorkspacePathResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // All methods below take the project's unique workspace folder key (see
    // GetWorkspaceFolder), NOT the raw display name, so two projects whose names
    // normalise to the same folder never share a workspace.
    public string GetProjectWorkspacePath(string projectKey)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, MakeSafeFolderName(projectKey)));

        // Defence in depth: even after sanitising the folder name, assert the result stays
        // directly under the configured root before any caller uses it for file IO.
        if (!IsWithin(root, full))
            throw new InvalidOperationException("Invalid project workspace path.");

        return full;
    }

    public string GetProjectDocsPath(string projectKey) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), "docs");

    public string GetDraftDocsPath(string projectKey) =>
        Path.Combine(GetProjectDocsPath(projectKey), "draft");

    public string GetVersionDocsPath(string projectKey, string versionName) =>
        Path.Combine(GetProjectDocsPath(projectKey), versionName);

    public string GetMockupPath(string projectKey) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), "03_Implementation", "poc-demo.html");

    public string GetPhasePath(string projectKey, string phase) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), phase);

    public string GetPhaseDraftPath(string projectKey, string phase) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), phase, "draft");

    public string GetPhaseVersionPath(string projectKey, string phase, string versionName) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), phase, versionName);

    public string GetSafeFullPath(string workspacePath, string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsWithin(root, fullPath))
            throw new InvalidOperationException("Invalid file path.");

        // The string check above is necessary but not sufficient: a symlink/junction inside
        // the workspace can point outside it, so a textually-valid path may resolve
        // elsewhere (a classic TOCTOU escape). Resolve the real location of the deepest
        // existing component (the target file may not exist yet) for BOTH root and target
        // and re-check. Resolution problems are treated as "couldn't prove an escape" and
        // allowed, so this never blocks legitimate writes — in normal use (no links) it is a
        // no-op because ResolveLinkTarget returns null and the resolved paths equal the
        // textual ones. NOTE: best-effort — it does not canonicalize every intermediate path
        // component, only the deepest existing one.
        try
        {
            var realRoot = ResolveExistingReal(root);
            var realFull = ResolveExistingReal(fullPath);
            if (realRoot != null && realFull != null && !IsWithin(realRoot, realFull))
                throw new InvalidOperationException("Invalid file path (resolves outside the workspace).");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Any IO/permission error during resolution must not break legitimate writes.
        }

        return fullPath;
    }

    // Path containment honouring the filesystem's own case sensitivity. Windows/macOS are
    // case-insensitive; Linux (the deploy target) is case-sensitive — the previous blanket
    // OrdinalIgnoreCase could both over- and under-match real directories on Linux.
    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // Canonical path of the deepest existing ancestor of <paramref name="path"/>, following
    // a symlink/junction on that component, with any not-yet-created tail re-appended.
    // Returns null when no ancestor exists.
    private static string? ResolveExistingReal(string path)
    {
        var tail = new List<string>();
        var current = path;

        while (!string.IsNullOrEmpty(current) && !File.Exists(current) && !Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                break;
            tail.Add(name);
            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrEmpty(current) || (!File.Exists(current) && !Directory.Exists(current)))
            return null;

        FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(current);

        for (var i = tail.Count - 1; i >= 0; i--)
            resolved = Path.Combine(resolved, tail[i]);

        return Path.GetFullPath(resolved);
    }

    /// <summary>
    /// Stable, unique folder name for a project's workspace. Derived from the project
    /// <paramref name="projectId"/> (not just the name) so two projects whose display
    /// names normalise to the same folder — e.g. "Task App" and "task-app" — no longer
    /// collide and overwrite each other's generated artifacts.
    /// </summary>
    public static string GetWorkspaceFolder(Guid projectId, string projectName) =>
        $"{MakeSafeFolderName(projectName)}-{projectId.ToString("N")[..8]}";

    public static string MakeSafeFolderName(string name)
    {
        name ??= "";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        // Path.GetInvalidFileNameChars() is OS-specific: on Linux it only covers '\0' and
        // '/', so path separators and parent-dir tokens survive and could climb out of the
        // workspace root. Neutralise them explicitly on every OS.
        name = name.Replace('/', '-').Replace('\\', '-').Replace("..", "-");
        name = name.Replace(" ", "-").ToLowerInvariant();

        // Never let the whole thing collapse to empty or a bare dot segment.
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            name = "workspace";

        return name;
    }
}
