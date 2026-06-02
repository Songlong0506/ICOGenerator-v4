using ICOGenerator.Services.Workspace;

namespace ICOGenerator.Services.Artifacts;

public class LocalArtifactStorage : IArtifactStorage
{
    private readonly WorkspacePathResolver _workspacePathResolver;

    public LocalArtifactStorage(WorkspacePathResolver workspacePathResolver)
    {
        _workspacePathResolver = workspacePathResolver;
    }

    public string GetDraftPath(string projectName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetDraftDocsPath(projectName), artifact.FileName);

    public string GetVersionPath(string projectName, string versionName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetVersionDocsPath(projectName, versionName), artifact.FileName);
}
