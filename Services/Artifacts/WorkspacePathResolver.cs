namespace ICOGenerator.Services.Artifacts;

public class WorkspacePathResolver
{
    private readonly IConfiguration _configuration;

    public WorkspacePathResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // Methods below take the unique workspace folder key (see GetWorkspaceFolder), NOT the raw
    // display name, so two projects whose names normalise to the same folder never collide.
    public string GetProjectWorkspacePath(string projectKey)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, MakeSafeFolderName(projectKey)));

        // Defence in depth: assert the sanitised result stays under the configured root before any IO.
        if (!IsWithin(root, full))
            throw new InvalidOperationException("Invalid project workspace path.");

        return full;
    }

    public string GetProjectDocsPath(string projectKey) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), "docs");

    public string GetMockupPath(string projectKey) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), "04_Implementation", "poc-demo.html");

    // Multi-file source the Developer agent generates lives under 04_Implementation/src (see
    // Prompts/Workflow/implementation.v1.md). This is what the "download source" feature packages.
    public string GetImplementationSourcePath(string projectKey) =>
        Path.Combine(GetProjectWorkspacePath(projectKey), "04_Implementation", "src");

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

        // The string check isn't sufficient: a symlink/junction inside the workspace can point
        // outside it (a TOCTOU escape). Resolve the deepest existing component of both root and
        // target and re-check. Resolution failures are allowed (never block legitimate writes);
        // best-effort — only the deepest existing component is canonicalized, not every level.
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

    // Path containment honouring the OS case sensitivity (Windows/macOS insensitive, Linux
    // sensitive); a blanket OrdinalIgnoreCase mis-matched real directories on Linux.
    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // Canonical path of the deepest existing ancestor (following a symlink/junction there), with
    // any not-yet-created tail re-appended. Returns null when no ancestor exists.
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
    /// Stable, unique workspace folder name. Includes <paramref name="projectId"/> (not just the
    /// name) so projects whose names normalise alike — e.g. "Task App" and "task-app" — don't collide.
    /// </summary>
    public static string GetWorkspaceFolder(Guid projectId, string projectName) =>
        $"{MakeSafeFolderName(projectName)}-{projectId.ToString("N")[..8]}";

    public static string MakeSafeFolderName(string name)
    {
        name ??= "";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        // GetInvalidFileNameChars() is OS-specific (on Linux only '\0' and '/'), so separators and
        // parent-dir tokens could survive and climb out of the root. Neutralise them on every OS.
        name = name.Replace('/', '-').Replace('\\', '-').Replace("..", "-");
        name = name.Replace(" ", "-").ToLowerInvariant();

        // Never let the whole thing collapse to empty or a bare dot segment.
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            name = "workspace";

        return name;
    }
}
