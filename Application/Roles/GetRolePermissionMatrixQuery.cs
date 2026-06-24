using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>Dựng ma trận quyền hiện tại từ bảng RolePermission để hiển thị màn hình cấu hình.</summary>
public class GetRolePermissionMatrixQuery
{
    private readonly AppDbContext _db;

    public GetRolePermissionMatrixQuery(AppDbContext db) => _db = db;

    public async Task<RolePermissionMatrixVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.RolePermissions
            .Select(x => new { x.Role, x.Permission })
            .ToListAsync(cancellationToken);

        HashSet<AppPermission> GrantedFor(UserRole role) =>
            rows.Where(x => x.Role == role).Select(x => x.Permission).ToHashSet();

        var columns = new List<RolePermissionColumn>
        {
            new() { Role = UserRole.Admin,   Title = UserRole.Admin.GetTitle(),   Locked = true },
            new() { Role = UserRole.TeamDev, Title = UserRole.TeamDev.GetTitle(), Granted = GrantedFor(UserRole.TeamDev) },
            new() { Role = UserRole.User,    Title = UserRole.User.GetTitle(),    Granted = GrantedFor(UserRole.User) },
        };

        return new RolePermissionMatrixVm { Columns = columns };
    }
}
