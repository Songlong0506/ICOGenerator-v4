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
    /// <summary>Tập quyền được cấp cho một role (tên role IS4, so khớp sau chuẩn hoá). Admin luôn có thêm
    /// <see cref="PermissionService.AdminLockedPermission"/>.</summary>
    Task<IReadOnlySet<AppPermission>> GetGrantedAsync(string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// True nếu người dùng có quyền <paramref name="permission"/> ở BẤT KỲ vai trò nào họ giữ
    /// (đọc mọi claim Role của principal).
    /// </summary>
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default);

    /// <summary>Xóa cache sau khi cập nhật RolePermission để lần kiểm tra kế tiếp đọc lại từ DB.</summary>
    void InvalidateCache();
}
