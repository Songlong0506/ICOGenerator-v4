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

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        var role = GetRole(user);
        if (role is null)
            return false;

        var granted = await GetGrantedAsync(role.Value, cancellationToken);
        return granted.Contains(permission);
    }

    public void InvalidateCache() => Interlocked.Increment(ref _cacheVersion);

    private static UserRole? GetRole(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(value, out var role) ? role : null;
    }
}
