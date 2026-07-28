using System.Security.Claims;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Nguồn sự thật duy nhất cho việc kiểm tra quyền: dùng bởi cả authorization filter
/// (<see cref="RequirePermissionAttribute"/>) lẫn _Layout/Views (lọc menu). Kết quả được cache
/// trong bộ nhớ và làm mới khi admin lưu lại ma trận quyền, nên thay đổi có hiệu lực ngay mà
/// không cần đăng nhập lại.
/// </summary>
public interface IPermissionService
{
    /// <summary>Tập quyền được cấp cho một role. SuperAdmin luôn nhận TOÀN BỘ quyền (implicit-all).</summary>
    Task<IReadOnlySet<AppPermission>> GetGrantedAsync(UserRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// HỢP quyền của nhiều role — dùng cho người giữ nhiều vai trò. Quyền giữa các vai trò giao nhau chứ
    /// không lồng nhau nên KHÔNG được thay bằng quyền của riêng vai trò "cao nhất".
    /// </summary>
    Task<IReadOnlySet<AppPermission>> GetGrantedAsync(IEnumerable<UserRole> roles, CancellationToken cancellationToken = default);

    /// <summary>
    /// True nếu người dùng có quyền <paramref name="permission"/> ở BẤT KỲ vai trò nào họ giữ
    /// (đọc mọi claim Role của principal).
    /// </summary>
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default);

    /// <summary>Xóa cache sau khi cập nhật RolePermission để lần kiểm tra kế tiếp đọc lại từ DB.</summary>
    void InvalidateCache();
}
