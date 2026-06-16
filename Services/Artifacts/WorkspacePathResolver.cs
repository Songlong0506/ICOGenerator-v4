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

        return Path.GetFullPath(Path.Combine(rootPath, MakeSafeFolderName(projectKey)));
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

        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid file path.");
        }

        return fullPath;
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
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}
