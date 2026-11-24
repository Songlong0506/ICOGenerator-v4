using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Lưu lại ma trận quyền. Popup gửi lên hai danh sách:
/// <list type="bullet">
/// <item><c>roles</c> — các cột (tên role IS4) đang hiển thị: CHỈ quyền của những role này bị thay thế. Role
/// không có trên popup (vd IS4 lỗi lúc mở trang nên cột vắng mặt) giữ nguyên, không bị xoá nhầm.</item>
/// <item><c>granted</c> — các ô được tích, dạng "ROLE:Permission". Ô thuộc role không nằm trong
/// <c>roles</c> bị bỏ qua.</item>
/// </list>
/// Ô khóa của Admin (<see cref="PermissionService.AdminLockedPermission"/>) không có checkbox nên không bao giờ
/// được gửi lên — không sao, PermissionService cấp nó bất kể DB. Sau khi lưu sẽ xóa cache của PermissionService
/// để thay đổi có hiệu lực ngay.
/// </summary>
public class UpdateRolePermissionsUseCase
{
    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditLogger _audit;

    public UpdateRolePermissionsUseCase(AppDbContext db, IPermissionService permissions, IAuditLogger audit)
    {
        _db = db;
        _permissions = permissions;
        _audit = audit;
    }

    public async Task ExecuteAsync(
        IEnumerable<string>? roles, IEnumerable<string>? granted, CancellationToken cancellationToken = default)
    {
        var editableRoles = (roles ?? Enumerable.Empty<string>())
            .Select(RoleNames.Normalize)
            .Where(r => r.Length is > 0 and <= RoleNames.MaxLength)
            .ToHashSet(StringComparer.Ordinal);
        if (editableRoles.Count == 0)
            return;

        var selected = ParseSelections(granted, editableRoles);

        // Thay thế toàn bộ quyền của các role trên popup bằng tập vừa chọn (đơn giản và idempotent).
        var existing = await _db.RolePermissions
            .Where(x => editableRoles.Contains(x.Role))
            .ToListAsync(cancellationToken);

        // Chụp quyền TRƯỚC khi thay để audit log so sánh được "role X được mở/thu quyền nào".
        var before = SnapshotPermissions(editableRoles, existing.GroupBy(x => x.Role)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission)));

        _db.RolePermissions.RemoveRange(existing);

        foreach (var (role, perms) in selected)
            foreach (var permission in perms)
                _db.RolePermissions.Add(new RolePermission { Role = role, Permission = permission });

        await _db.SaveChangesAsync(cancellationToken);
        _permissions.InvalidateCache();

        await _audit.LogAsync(AuditCategory.Role, AuditAction.Update, "RolePermissions",
            Loc.T("UpdateRolePermissionsUseCase.ExecuteAsync"),
            before: before,
            after: SnapshotPermissions(editableRoles,
                selected.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<AppPermission>)kvp.Value)),
            cancellationToken: cancellationToken);
    }

    // Đưa quyền về dạng { "HCP_CBO_API.CBO.ADMIN": ["ProjectsView", ...], ... } đã sắp xếp để before/after dễ
    // so sánh bằng mắt.
    private static SortedDictionary<string, List<string>> SnapshotPermissions(
        IEnumerable<string> roles, IReadOnlyDictionary<string, IEnumerable<AppPermission>> byRole) =>
        new(roles.ToDictionary(
            role => role,
            role => (byRole.TryGetValue(role, out var perms) ? perms : Enumerable.Empty<AppPermission>())
                .Select(p => p.ToString())
                .OrderBy(p => p)
                .ToList()), StringComparer.Ordinal);

    // "HCP_CBO_API.CBO.TEAMDEV:ProjectsView" -> (HCP_CBO_API.CBO.TEAMDEV, ProjectsView). Tách ở dấu ':' CUỐI
    // (tên quyền không chứa ':'). Bỏ qua chuỗi sai định dạng, role không có trên popup, và quyền trùng.
    private static Dictionary<string, HashSet<AppPermission>> ParseSelections(
        IEnumerable<string>? granted, IReadOnlySet<string> editableRoles)
    {
        var result = editableRoles.ToDictionary(r => r, _ => new HashSet<AppPermission>(), StringComparer.Ordinal);
        if (granted is null)
            return result;

        foreach (var item in granted)
        {
            var separator = item?.LastIndexOf(':') ?? -1;
            if (separator <= 0)
                continue;
            var role = RoleNames.Normalize(item![..separator]);
            if (!result.TryGetValue(role, out var perms))
                continue;
            if (Enum.TryParse<AppPermission>(item[(separator + 1)..], out var permission))
                perms.Add(permission);
        }

        return result;
    }
}
