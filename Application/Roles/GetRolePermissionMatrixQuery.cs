using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Identity;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Dựng ma trận quyền cho popup Roles &amp; Permissions. Cột = danh sách role của API resource lấy TRỰC TIẾP
/// từ IdentityServer (cùng nguồn với bảng role trên trang User Roles) — role mới tạo bên IS4 hiện ngay, không
/// có danh sách cứng nào trong code. Ghép thêm (xem <see cref="BuildColumns"/>):
/// <list type="bullet">
/// <item>role đang có quyền trong DB nhưng IS4 không trả về (đã xoá/đổi tên bên IS4, hoặc IS4 lỗi) — vẫn
/// hiện để admin thấy và bỏ tick được (khi đã lấy được danh sách IS4 thì đánh dấu
/// <see cref="RolePermissionColumn.MissingFromIdentityServer"/>);</item>
/// <item>role Admin (<see cref="RoleNames.Admin"/>) — luôn có ô khóa nên luôn có cột.</item>
/// </list>
/// IS4 lỗi/không với tới/phiên hết hạn hoặc chạy đăng nhập cục bộ ⇒ không chặn trang: ma trận chỉ còn các
/// role lấy từ DB và <see cref="RolePermissionMatrixVm.IdentityServerUnavailable"/> bật để view báo cho admin.
/// </summary>
public class GetRolePermissionMatrixQuery
{
    private readonly AppDbContext _db;
    private readonly IdentityServerService _identityServer;
    private readonly IdentityServerSettings _authSettings;
    private readonly ILogger<GetRolePermissionMatrixQuery> _logger;

    public GetRolePermissionMatrixQuery(
        AppDbContext db,
        IdentityServerService identityServer,
        IdentityServerSettings authSettings,
        ILogger<GetRolePermissionMatrixQuery> logger)
    {
        _db = db;
        _identityServer = identityServer;
        _authSettings = authSettings;
        _logger = logger;
    }

    public async Task<RolePermissionMatrixVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.RolePermissions
            .Select(x => new { x.Role, x.Permission })
            .ToListAsync(cancellationToken);
        var granted = rows
            .GroupBy(x => RoleNames.Normalize(x.Role))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Permission).ToHashSet());

        var identityServerRoles = await TryGetIdentityServerRolesAsync();

        return new RolePermissionMatrixVm
        {
            Columns = BuildColumns(identityServerRoles, granted),
            IdentityServerUnavailable = identityServerRoles is null
        };
    }

    /// <summary>
    /// Ghép cột theo thứ tự: role IS4 (giữ thứ tự IS4 trả về), rồi role chỉ còn trong DB, rồi Admin nếu chưa
    /// có. Tên role so khớp sau chuẩn hoá; trùng/rỗng bị bỏ. <paramref name="identityServerRoles"/> null =
    /// không lấy được từ IS4.
    /// </summary>
    public static IReadOnlyList<RolePermissionColumn> BuildColumns(
        IEnumerable<string>? identityServerRoles,
        IReadOnlyDictionary<string, HashSet<AppPermission>> granted)
    {
        var columns = new List<RolePermissionColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var identityServerKnown = identityServerRoles is not null;

        void Add(string raw, bool onIdentityServer)
        {
            var role = RoleNames.Normalize(raw);
            if (role.Length == 0 || role.Length > RoleNames.MaxLength || !seen.Add(role))
                return;
            columns.Add(new RolePermissionColumn
            {
                Role = role,
                Title = RoleNames.ShortLabel(role),
                MissingFromIdentityServer = identityServerKnown && !onIdentityServer,
                Granted = granted.TryGetValue(role, out var perms) ? perms : new HashSet<AppPermission>()
            });
        }

        foreach (var role in identityServerRoles ?? Enumerable.Empty<string>())
            Add(role, onIdentityServer: true);
        foreach (var role in granted.Keys.Order(StringComparer.Ordinal))
            Add(role, onIdentityServer: false);
        Add(RoleNames.Admin, onIdentityServer: false);

        return columns;
    }

    // Null = không lấy được (chế độ cục bộ không có IdP, phiên hết hạn, IS4 lỗi). Chế độ cục bộ bỏ qua hẳn
    // lời gọi: không có access_token nên chắc chắn hỏng, mà BaseURL trỏ tới host không với tới sẽ treo trang
    // tới khi HttpClient timeout.
    private async Task<IReadOnlyList<string>?> TryGetIdentityServerRolesAsync()
    {
        if (_authSettings.IsLocalLogin)
            return null;

        try
        {
            var roles = await _identityServer.GetAllRolesAsync();
            return roles.Select(r => r.Name).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch the role list from IdentityServer for the permission matrix.");
            return null;
        }
    }
}
