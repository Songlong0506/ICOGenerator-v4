
namespace ICOGenerator.Services.Artifacts;

public class LocalArtifactStorage : IArtifactStorage
{
    private readonly WorkspacePathResolver _workspacePathResolver;

    public LocalArtifactStorage(WorkspacePathResolver workspacePathResolver)
    {
        _workspacePathResolver = workspacePathResolver;
    }

    public void InitializeProjectWorkspace(string projectName)
    {
        Directory.CreateDirectory(_workspacePathResolver.GetProjectWorkspacePath(projectName));
        foreach (var phase in ProjectWorkspaceLayout.Phases)
            Directory.CreateDirectory(_workspacePathResolver.GetPhasePath(projectName, phase));
    }

    public string GetDraftPath(string projectName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseDraftPath(projectName, artifact.Phase), artifact.FileName);

    public string GetVersionPath(string projectName, string versionName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseVersionPath(projectName, artifact.Phase, versionName), artifact.FileName);
}
