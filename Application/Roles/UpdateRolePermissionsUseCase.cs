using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Lưu lại ma trận quyền cho các role CHỈNH ĐƯỢC (TeamDev, User). Admin bị bỏ qua hoàn toàn vì
/// luôn có toàn quyền. Nhận danh sách chuỗi "Role:Permission" từ các checkbox được tích.
/// Sau khi lưu sẽ xóa cache của PermissionService để thay đổi có hiệu lực ngay.
/// </summary>
public class UpdateRolePermissionsUseCase
{
    // Admin không nằm ở đây: không bao giờ ghi quyền cho admin (implicit-all).
    private static readonly UserRole[] EditableRoles = { UserRole.TeamDev, UserRole.User };

    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;

    public UpdateRolePermissionsUseCase(AppDbContext db, IPermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public async Task ExecuteAsync(IEnumerable<string>? granted, CancellationToken cancellationToken = default)
    {
        var selected = ParseSelections(granted);

        // Thay thế toàn bộ quyền của các role chỉnh được bằng tập vừa chọn (đơn giản và idempotent).
        var existing = await _db.RolePermissions
            .Where(x => EditableRoles.Contains(x.Role))
            .ToListAsync(cancellationToken);
        _db.RolePermissions.RemoveRange(existing);

        foreach (var role in EditableRoles)
            if (selected.TryGetValue(role, out var perms))
                foreach (var permission in perms)
                    _db.RolePermissions.Add(new RolePermission { Role = role, Permission = permission });

        await _db.SaveChangesAsync(cancellationToken);
        _permissions.InvalidateCache();
    }

    // "TeamDev:ProjectsView" -> (TeamDev, ProjectsView). Bỏ qua chuỗi sai định dạng, role không chỉnh được,
    // và quyền trùng (HashSet). Admin có lọt vào cũng bị loại vì không nằm trong EditableRoles.
    private static Dictionary<UserRole, HashSet<AppPermission>> ParseSelections(IEnumerable<string>? granted)
    {
        var result = EditableRoles.ToDictionary(r => r, _ => new HashSet<AppPermission>());
        if (granted is null)
            return result;

        foreach (var item in granted)
        {
            var parts = item?.Split(':', 2);
            if (parts is not { Length: 2 })
                continue;
            if (!Enum.TryParse<UserRole>(parts[0], out var role) || !result.ContainsKey(role))
                continue;
            if (Enum.TryParse<AppPermission>(parts[1], out var permission))
                result[role].Add(permission);
        }

        return result;
    }
}
