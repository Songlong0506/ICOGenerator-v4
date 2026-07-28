using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một vai trò được gán cho một người dùng — quan hệ NHIỀU-NHIỀU giữa <see cref="AppUser"/> và
/// <see cref="UserRole"/>, thay cho cột <c>AppUser.Role</c> đơn trị trước đây. Lý do đổi: quyền của các
/// vai trò GIAO NHAU chứ không lồng nhau, nên cách cũ (gộp về vai trò "cao nhất" — xem
/// <c>IdentityServerSettings.MapRoles</c>) sẽ làm mất những quyền chỉ vai trò thấp hơn mới có.
/// Quyền hiệu lực của một người = HỢP của quyền mọi vai trò họ giữ (xem <c>PermissionService</c>).
/// Khóa chính là cặp (AppUserId, Role) nên DB tự chặn gán trùng một vai trò hai lần.
/// Nguồn sự thật vẫn là IdentityServer: mỗi lần đăng nhập SSO, tập vai trò được đồng bộ lại từ role
/// claim (xem <c>SsoUserProvisioner</c>).
/// </summary>
public class AppUserRole
{
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public UserRole Role { get; set; }
}
