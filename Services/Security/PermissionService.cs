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
    // SuperAdmin được coi là có toàn quyền: tính sẵn 1 lần, không phụ thuộc DB để không bao giờ tự khóa mình.
    private static readonly IReadOnlySet<AppPermission> AllPermissions =
        Enum.GetValues<AppPermission>().ToHashSet();

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private static int _cacheVersion;

    public PermissionService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlySet<AppPermission>> GetGrantedAsync(UserRole role, CancellationToken cancellationToken = default)
    {
        if (role == UserRole.SuperAdmin)
            return AllPermissions;

        var key = $"perms:{Volatile.Read(ref _cacheVersion)}:{role}";
        if (_cache.TryGetValue(key, out IReadOnlySet<AppPermission>? cached) && cached is not null)
            return cached;

        var granted = (await _db.RolePermissions
                .Where(x => x.Role == role)
                .Select(x => x.Permission)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        _cache.Set(key, (IReadOnlySet<AppPermission>)granted, TimeSpan.FromMinutes(10));
        return granted;
    }

    public async Task<IReadOnlySet<AppPermission>> GetGrantedAsync(IEnumerable<UserRole> roles, CancellationToken cancellationToken = default)
    {
        var distinct = roles.Distinct().ToList();

        // Một vai trò toàn quyền là đủ — khỏi truy vấn các vai trò còn lại.
        if (distinct.Contains(UserRole.SuperAdmin))
            return AllPermissions;

        var union = new HashSet<AppPermission>();
        foreach (var role in distinct)
            union.UnionWith(await GetGrantedAsync(role, cancellationToken));
        return union;
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
    /// Các vai trò đọc từ claim Role của principal. Claim không ánh xạ được về <see cref="UserRole"/>
    /// (vai trò cũ đã bỏ, hoặc claim lạ do IdP phát) bị BỎ QUA thay vì làm hỏng cả lượt kiểm tra quyền.
    /// </summary>
    private static IEnumerable<UserRole> GetRoles(ClaimsPrincipal user)
    {
        foreach (var claim in user.FindAll(ClaimTypes.Role))
        {
            if (Enum.TryParse<UserRole>(claim.Value, out var role))
                yield return role;
        }
    }
}
