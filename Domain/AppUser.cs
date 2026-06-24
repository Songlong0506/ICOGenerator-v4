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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
