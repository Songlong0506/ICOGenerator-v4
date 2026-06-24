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
    /// <summary>Tập quyền được cấp cho một role. Admin luôn nhận TOÀN BỘ quyền (implicit-all).</summary>
    Task<IReadOnlySet<AppPermission>> GetGrantedAsync(UserRole role, CancellationToken cancellationToken = default);

    /// <summary>True nếu người dùng (đọc role từ claim) có quyền <paramref name="permission"/>.</summary>
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default);

    /// <summary>Xóa cache sau khi cập nhật RolePermission để lần kiểm tra kế tiếp đọc lại từ DB.</summary>
    void InvalidateCache();
}
