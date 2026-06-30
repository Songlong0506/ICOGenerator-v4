using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Tài khoản người dùng đăng nhập. Thay thế cơ chế login dùng chung 1 credential trong config:
/// mật khẩu được băm (PasswordHasher của ASP.NET) và mỗi user gắn đúng một <see cref="UserRole"/>.
/// Bộ user được seed sẵn trong DbInitializer (admin/teamdev/user), chưa có UI tạo user.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsActive { get; set; } = true;
    // Bộ nhớ dài hạn về CHÍNH người dùng này, gom XUYÊN SUỐT mọi project họ tạo (khác với
    // Project.ConversationSummary chỉ nhớ trong một dự án). Là một hồ sơ ngắn gọn các sự thật BỀN về
    // user — vai trò, lĩnh vực/ngành, tổ chức, văn phong/định dạng tài liệu họ ưa, thuật ngữ hay dùng,
    // ràng buộc lặp lại — được BA chắt lọc DẦN từ hội thoại và nạp lại ở mọi cuộc để "càng nói càng hiểu
    // user". null = chưa chắt lọc được gì. Xem UserMemoryService.
    public string? UserMemory { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
