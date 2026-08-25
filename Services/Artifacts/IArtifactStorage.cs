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

    /// <summary>
    /// Chép workspace của một project sang project khác khi NHÂN BẢN dự án (xem CloneProjectUseCase).
    /// Khác <see cref="TryRenameProjectWorkspace"/>: bản gốc phải còn nguyên chỗ cũ.
    /// <paramref name="onlyTopLevelFolders"/> null = chép cả cây; khác null = chỉ chép các thư mục cấp 1 có
    /// tên trong đó (bản sao "chỉ phần yêu cầu" chỉ cần <c>00_Source</c>). Các thư mục sinh lại được
    /// (<see cref="WorkspaceFileFilter.RegenerableDirectories"/>) luôn bị bỏ qua.
    /// Trả về <c>true</c> khi đã chép xong HOẶC khi không có gì để chép (nguồn chưa có trên đĩa, RootPath
    /// cấu hình sai) — cùng tinh thần best-effort với lúc tạo project. Trả về <c>false</c> khi nguồn CÓ trên
    /// đĩa nhưng không chép được, hoặc thư mục đích đã tồn tại: caller phải hủy việc nhân bản để không tạo
    /// ra một project trỏ vào thư mục trống.
    /// </summary>
    bool TryCopyProjectWorkspace(string sourceProjectKey, string targetProjectKey, IReadOnlyCollection<string>? onlyTopLevelFolders = null);

    /// <summary>Xóa thư mục workspace (best-effort) — dùng để hoàn tác khi nhân bản chép đĩa xong nhưng lưu DB lỗi.</summary>
    void TryDeleteProjectWorkspace(string projectKey);

    string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact);
    string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact);

    /// <summary>Thư mục chứa tài liệu nguồn (ảnh/PDF) người dùng upload cho project (input, không phải output đã sinh).</summary>
    string GetSourceUploadDir(string projectKey);
}
