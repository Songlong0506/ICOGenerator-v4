namespace ICOGenerator.Domain;

/// <summary>
/// Một người được MỜI vào project với vai trò reviewer/stakeholder: thấy project trong danh sách của
/// mình (dù không phải người tạo), xem workspace, góp ý trên Product Brief và annotate POC. Thành viên
/// KHÔNG thay chủ project — quyền chat BA/duyệt vẫn theo AppPermission như trước. Username lưu dạng
/// chuỗi khớp <see cref="AppUser.Username"/> (không FK — nhất quán với CreatedByUsername trên Project).
/// </summary>
public class ProjectMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Username người được mời (khớp <see cref="AppUser.Username"/>).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Ai đã mời (thường là chủ project). Chỉ để hiển thị/tra soát.</summary>
    public string? AddedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
