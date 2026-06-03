namespace ICOGenerator.Services.Artifacts;

public interface IArtifactStorage
{
    string GetDraftPath(string projectName, ProjectArtifactDescriptor artifact);
    string GetVersionPath(string projectName, string versionName, ProjectArtifactDescriptor artifact);
}
