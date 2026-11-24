using System.Security.Claims;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Đọc quyền của role từ bảng RolePermission, cache theo role. Cache được "phiên bản hóa" bằng
/// một số nguyên (_cacheVersion): InvalidateCache() tăng phiên bản nên các key cũ tự động bị bỏ qua
/// (đơn giản và an toàn với mọi role mà không cần liệt kê từng key để remove).
/// </summary>
public class PermissionService : IPermissionService
{
    /// <summary>
    /// Quyền Admin LUÔN có, không phụ thuộc DB: không còn vai trò nào toàn quyền ngầm định nên nếu Admin tự
    /// bỏ tick quyền quản lý ma trận thì không ai mở lại được nữa ngoài sửa tay DB. Popup Permissions hiển
    /// thị ô này dạng khóa.
    /// </summary>
    public const AppPermission AdminLockedPermission = AppPermission.AdministrationManageRoles;

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private static int _cacheVersion;

    public PermissionService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlySet<AppPermission>> GetGrantedAsync(string role, CancellationToken cancellationToken = default)
    {
        role = RoleNames.Normalize(role);
        var key = $"perms:{Volatile.Read(ref _cacheVersion)}:{role}";
        if (_cache.TryGetValue(key, out IReadOnlySet<AppPermission>? cached) && cached is not null)
            return cached;

        var granted = (await _db.RolePermissions
                .Where(x => x.Role == role)
                .Select(x => x.Permission)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        if (role == RoleNames.Admin)
            granted.Add(AdminLockedPermission);

        _cache.Set(key, (IReadOnlySet<AppPermission>)granted, TimeSpan.FromMinutes(10));
        return granted;
    }

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        // Người dùng có thể giữ NHIỀU vai trò (nhiều claim Role) và quyền của các vai trò GIAO NHAU chứ
        // không lồng nhau ⇒ phải duyệt hết, có một vai trò được cấp là đủ. Dừng sớm ở vai trò đầu tiên
        // khớp để khỏi nạp quyền của các vai trò còn lại.
        foreach (var role in GetRoles(user))
        {
            var granted = await GetGrantedAsync(role, cancellationToken);
            if (granted.Contains(permission))
                return true;
        }
        return false;
    }

    public void InvalidateCache() => Interlocked.Increment(ref _cacheVersion);

    /// <summary>
    /// Các role đọc từ claim Role của principal — chính là tên role IS4 (đã chuẩn hoá khi phát claim).
    /// Role chưa có dòng nào trong bảng quyền đơn giản là không được cấp gì, không làm hỏng lượt kiểm tra.
    /// </summary>
    private static IEnumerable<string> GetRoles(ClaimsPrincipal user) =>
        user.FindAll(ClaimTypes.Role)
            .Select(c => RoleNames.Normalize(c.Value))
            .Where(r => r.Length > 0)
            .Distinct();
}
