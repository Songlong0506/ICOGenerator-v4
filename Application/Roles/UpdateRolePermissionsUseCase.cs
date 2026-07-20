using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Lưu lại ma trận quyền cho các role CHỈNH ĐƯỢC (Admin, TeamDev, User). SuperAdmin bị bỏ qua hoàn toàn
/// vì luôn có toàn quyền. Nhận danh sách chuỗi "Role:Permission" từ các checkbox được tích.
/// Sau khi lưu sẽ xóa cache của PermissionService để thay đổi có hiệu lực ngay.
/// </summary>
public class UpdateRolePermissionsUseCase
{
    // SuperAdmin không nằm ở đây: không bao giờ ghi quyền cho super admin (implicit-all).
    private static readonly UserRole[] EditableRoles = { UserRole.Admin, UserRole.TeamDev, UserRole.User };

    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditLogger _audit;

    public UpdateRolePermissionsUseCase(AppDbContext db, IPermissionService permissions, IAuditLogger audit)
    {
        _db = db;
        _permissions = permissions;
        _audit = audit;
    }

    public async Task ExecuteAsync(IEnumerable<string>? granted, CancellationToken cancellationToken = default)
    {
        var selected = ParseSelections(granted);

        // Thay thế toàn bộ quyền của các role chỉnh được bằng tập vừa chọn (đơn giản và idempotent).
        var existing = await _db.RolePermissions
            .Where(x => EditableRoles.Contains(x.Role))
            .ToListAsync(cancellationToken);

        // Chụp quyền TRƯỚC khi thay để audit log so sánh được "role X được mở/thu quyền nào".
        var before = SnapshotPermissions(existing.GroupBy(x => x.Role)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission)));

        _db.RolePermissions.RemoveRange(existing);

        foreach (var role in EditableRoles)
            if (selected.TryGetValue(role, out var perms))
                foreach (var permission in perms)
                    _db.RolePermissions.Add(new RolePermission { Role = role, Permission = permission });

        await _db.SaveChangesAsync(cancellationToken);
        _permissions.InvalidateCache();

        await _audit.LogAsync(AuditCategory.Role, AuditAction.Update, "RolePermissions",
            "Cập nhật ma trận quyền của các role",
            before: before,
            after: SnapshotPermissions(selected.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<AppPermission>)kvp.Value)),
            cancellationToken: cancellationToken);
    }

    // Đưa quyền về dạng { "TeamDev": ["ProjectsView", ...], ... } đã sắp xếp để before/after dễ so sánh bằng mắt.
    private static Dictionary<string, List<string>> SnapshotPermissions(
        IReadOnlyDictionary<UserRole, IEnumerable<AppPermission>> byRole) =>
        EditableRoles.ToDictionary(
            role => role.ToString(),
            role => (byRole.TryGetValue(role, out var perms) ? perms : Enumerable.Empty<AppPermission>())
                .Select(p => p.ToString())
                .OrderBy(p => p)
                .ToList());

    // "TeamDev:ProjectsView" -> (TeamDev, ProjectsView). Bỏ qua chuỗi sai định dạng, role không chỉnh được,
    // và quyền trùng (HashSet). SuperAdmin có lọt vào cũng bị loại vì không nằm trong EditableRoles.
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
