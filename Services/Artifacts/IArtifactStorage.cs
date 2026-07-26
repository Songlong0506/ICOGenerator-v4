namespace ICOGenerator.Services.Artifacts;

public interface IArtifactStorage
{
    /// <summary>Tạo bộ khung thư mục giai đoạn cho một project mới (best-effort).</summary>
    void InitializeProjectWorkspace(string projectKey);

    /// <summary>
    /// Đổi tên thư mục workspace khi project được đổi tên (tên thư mục dẫn xuất từ tên project — xem
    /// <see cref="WorkspacePathResolver.GetWorkspaceFolder"/>), để tài liệu/POC đã sinh vẫn nằm đúng chỗ
    /// mà mọi đường dẫn tính lại từ tên MỚI vẫn trỏ tới.
    /// Trả về <c>true</c> khi đã đổi xong HOẶC khi không có gì phải đổi (chưa có thư mục trên đĩa, hai key
    /// trùng nhau, RootPath cấu hình sai nên workspace chưa từng tồn tại). Trả về <c>false</c> khi thư mục
    /// CÓ trên đĩa nhưng không đổi tên được — caller phải hủy việc đổi tên để không bỏ rơi dữ liệu.
    /// </summary>
    bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey);

    string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact);
    string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact);

    /// <summary>Thư mục chứa tài liệu nguồn (ảnh/PDF) người dùng upload cho project (input, không phải output đã sinh).</summary>
    string GetSourceUploadDir(string projectKey);
}
