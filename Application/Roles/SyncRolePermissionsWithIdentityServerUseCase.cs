using ICOGenerator.Localization;
using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Identity;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Lấy danh sách role của API resource từ IdentityServer (nguồn cột của ma trận Roles &amp; Permissions) và
/// XOÁ quyền của role đã không còn bên IS4 (bị xoá/đổi tên) — để bảng <c>RolePermissions</c> không giữ quyền
/// mồ côi mà nếu IS4 tạo lại role trùng tên thì người giữ role mới tự dưng thừa hưởng. Trả về danh sách role
/// IS4 (đã chuẩn hoá) cho <see cref="GetRolePermissionMatrixQuery"/>, hoặc null khi không lấy được.
///
/// <para>Chốt chặn — CHỈ xoá khi chắc chắn danh sách IS4 là thật:</para>
/// <list type="bullet">
/// <item>chế độ cục bộ / phiên hết hạn / IS4 lỗi ⇒ null, không xoá gì;</item>
/// <item>danh sách RỖNG ⇒ coi như không lấy được (<c>GetAllRolesAsync</c> trả rỗng cả khi IS4 trả mã lỗi),
/// nếu không một lần IS4 trục trặc sẽ xoá sạch ma trận;</item>
/// <item>role Admin (<see cref="RoleNames.Admin"/>) không bao giờ bị xoá — nó là role seed giữ ô khóa.</item>
/// </list>
/// </summary>
public class SyncRolePermissionsWithIdentityServerUseCase
{
    private readonly AppDbContext _db;
    private readonly IdentityServerService _identityServer;
    private readonly IdentityServerSettings _authSettings;
    private readonly IPermissionService _permissions;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SyncRolePermissionsWithIdentityServerUseCase> _logger;

    public SyncRolePermissionsWithIdentityServerUseCase(
        AppDbContext db,
        IdentityServerService identityServer,
        IdentityServerSettings authSettings,
        IPermissionService permissions,
        IAuditLogger audit,
        ILogger<SyncRolePermissionsWithIdentityServerUseCase> logger)
    {
        _db = db;
        _identityServer = identityServer;
        _authSettings = authSettings;
        _permissions = permissions;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var roles = await TryGetIdentityServerRolesAsync();
        if (roles is null)
            return null;

        await PruneAsync(roles, cancellationToken);
        return roles;
    }

    /// <summary>
    /// Xoá quyền của mọi role không có trong <paramref name="identityServerRoles"/> (trừ Admin). Danh sách
    /// rỗng ⇒ không làm gì. Tách riêng để test được mà không cần IS4.
    /// </summary>
    public async Task PruneAsync(IReadOnlyCollection<string> identityServerRoles, CancellationToken cancellationToken = default)
    {
        var keep = identityServerRoles.Select(RoleNames.Normalize).Where(r => r.Length > 0)
            .Append(RoleNames.Admin)
            .ToHashSet(StringComparer.Ordinal);
        if (keep.Count <= 1)
            return;

        var orphaned = await _db.RolePermissions
            .Where(x => !keep.Contains(x.Role))
            .ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
            return;

        var removed = orphaned
            .GroupBy(x => x.Role)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission.ToString()).OrderBy(p => p).ToList());

        _db.RolePermissions.RemoveRange(orphaned);
        await _db.SaveChangesAsync(cancellationToken);
        _permissions.InvalidateCache();

        _logger.LogInformation("Removed permissions of roles no longer on IdentityServer: {Roles}.",
            string.Join(", ", removed.Keys));
        await _audit.LogAsync(AuditCategory.Role, AuditAction.Delete, "RolePermissions",
            Loc.T("SyncRolePermissionsWithIdentityServerUseCase.PruneAsync", string.Join(", ", removed.Keys)),
            before: removed,
            after: null,
            cancellationToken: cancellationToken);
    }

    // Null = không lấy được hoặc rỗng (xem chốt chặn ở summary). Chế độ cục bộ bỏ qua hẳn lời gọi: không có
    // access_token nên chắc chắn hỏng, mà BaseURL trỏ tới host không với tới sẽ treo trang tới khi timeout.
    private async Task<IReadOnlyList<string>?> TryGetIdentityServerRolesAsync()
    {
        if (_authSettings.IsLocalLogin)
            return null;

        try
        {
            var roles = (await _identityServer.GetAllRolesAsync())
                .Select(r => RoleNames.Normalize(r.Name))
                .Where(r => r.Length > 0)
                .Distinct()
                .ToList();
            return roles.Count > 0 ? roles : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch the role list from IdentityServer for the permission matrix.");
            return null;
        }
    }
}
