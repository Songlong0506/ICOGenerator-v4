namespace ICOGenerator.Services.Artifacts;

public interface IArtifactStorage
{
    /// <summary>Tạo bộ khung thư mục giai đoạn cho một project mới (best-effort).</summary>
    void InitializeProjectWorkspace(string projectKey);

    string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact);
    string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact);

    /// <summary>Thư mục chứa tài liệu nguồn (ảnh/PDF) người dùng upload cho project (input, không phải output đã sinh).</summary>
    string GetSourceUploadDir(string projectKey);
}
