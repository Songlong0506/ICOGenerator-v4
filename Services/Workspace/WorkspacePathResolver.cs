namespace ICOGenerator.Services.Workspace;

public class WorkspacePathResolver
{
    private readonly IConfiguration _configuration;

    public WorkspacePathResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetProjectWorkspacePath(string projectName)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        return Path.GetFullPath(Path.Combine(rootPath, MakeSafeFolderName(projectName)));
    }

    public string GetProjectDocsPath(string projectName) =>
        Path.Combine(GetProjectWorkspacePath(projectName), "docs");

    public string GetDraftDocsPath(string projectName) =>
        Path.Combine(GetProjectDocsPath(projectName), "draft");

    public string GetVersionDocsPath(string projectName, string versionName) =>
        Path.Combine(GetProjectDocsPath(projectName), versionName);

    public string GetMockupPath(string projectName) =>
        Path.Combine(GetProjectWorkspacePath(projectName), "poc-demo.html");

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

    public static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}
