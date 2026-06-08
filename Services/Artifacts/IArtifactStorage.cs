namespace ICOGenerator.Services.Artifacts;

public interface IArtifactStorage
{
    /// <summary>Tạo bộ khung thư mục giai đoạn cho một project mới (best-effort).</summary>
    void InitializeProjectWorkspace(string projectName);

    string GetDraftPath(string projectName, ProjectArtifactDescriptor artifact);
    string GetVersionPath(string projectName, string versionName, ProjectArtifactDescriptor artifact);
}
