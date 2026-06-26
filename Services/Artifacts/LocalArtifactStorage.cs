
namespace ICOGenerator.Services.Artifacts;

public class LocalArtifactStorage : IArtifactStorage
{
    private readonly WorkspacePathResolver _workspacePathResolver;

    public LocalArtifactStorage(WorkspacePathResolver workspacePathResolver)
    {
        _workspacePathResolver = workspacePathResolver;
    }

    public void InitializeProjectWorkspace(string projectKey)
    {
        Directory.CreateDirectory(_workspacePathResolver.GetProjectWorkspacePath(projectKey));
        foreach (var phase in ProjectWorkspaceLayout.Phases)
            Directory.CreateDirectory(_workspacePathResolver.GetPhasePath(projectKey, phase));
    }

    public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseDraftPath(projectKey, artifact.Phase), artifact.FileName);

    public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseVersionPath(projectKey, artifact.Phase, versionName), artifact.FileName);

    public string GetSourceUploadDir(string projectKey) =>
        Path.Combine(_workspacePathResolver.GetProjectWorkspacePath(projectKey), "00_Source");
}
