using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Dựng ma trận quyền cho popup Roles &amp; Permissions. Cột = danh sách role của API resource lấy TRỰC TIẾP
/// từ IdentityServer (do <see cref="SyncRolePermissionsWithIdentityServerUseCase"/> lấy và đồng thời xoá quyền
/// của role đã mất bên IS4) — role mới tạo bên IS4 hiện ngay, không có danh sách cứng nào trong code.
/// Không lấy được danh sách IS4 (<c>identityServerRoles</c> null: IS4 lỗi, phiên hết hạn, chế độ cục bộ) ⇒
/// không chặn trang: cột là các role đang có quyền trong DB và
/// <see cref="RolePermissionMatrixVm.IdentityServerUnavailable"/> bật để view báo cho admin.
/// Cột Admin (<see cref="RoleNames.Admin"/>) luôn có vì giữ ô khóa.
/// </summary>
public class GetRolePermissionMatrixQuery
{
    private readonly AppDbContext _db;

    public GetRolePermissionMatrixQuery(AppDbContext db) => _db = db;

    public async Task<RolePermissionMatrixVm> ExecuteAsync(
        IReadOnlyList<string>? identityServerRoles, CancellationToken cancellationToken = default)
    {
        var rows = await _db.RolePermissions
            .Select(x => new { x.Role, x.Permission })
            .ToListAsync(cancellationToken);
        var granted = rows
            .GroupBy(x => RoleNames.Normalize(x.Role))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission).ToHashSet());

        return new RolePermissionMatrixVm
        {
            Columns = BuildColumns(identityServerRoles, granted),
            IdentityServerUnavailable = identityServerRoles is null
        };
    }

    /// <summary>
    /// Ghép cột theo thứ tự: role IS4 (giữ thứ tự IS4 trả về) — hoặc, khi không có danh sách IS4, các role
    /// đang có quyền trong DB — rồi Admin nếu chưa có. Tên role so khớp sau chuẩn hoá; trùng/rỗng bị bỏ.
    /// </summary>
    public static IReadOnlyList<RolePermissionColumn> BuildColumns(
        IEnumerable<string>? identityServerRoles,
        IReadOnlyDictionary<string, HashSet<AppPermission>> granted)
    {
        var columns = new List<RolePermissionColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string raw)
        {
            var role = RoleNames.Normalize(raw);
            if (role.Length == 0 || role.Length > RoleNames.MaxLength || !seen.Add(role))
                return;
            columns.Add(new RolePermissionColumn
            {
                Role = role,
                Title = RoleNames.ShortLabel(role),
                Granted = granted.TryGetValue(role, out var perms) ? perms : new HashSet<AppPermission>()
            });
        }

        foreach (var role in identityServerRoles ?? granted.Keys.Order(StringComparer.Ordinal))
            Add(role);
        Add(RoleNames.Admin);

        return columns;
    }
}
