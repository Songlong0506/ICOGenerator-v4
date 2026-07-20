using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Identity;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Màn hình gán / thu hồi role cho người dùng trên Bosch IdentityServer (IS4). Trang chỉ render khung;
/// dữ liệu (danh sách role, gợi ý user LDAP, user theo role) nạp qua các endpoint JSON bằng AJAX, rồi
/// Assign/Withdraw gọi API IS4. Mọi lời gọi ra IS4 bọc try/catch để IS4 lỗi/không với tới (hoặc chạy ở
/// chế độ Local không có access_token) chỉ trả thông báo, không làm sập trang.
/// </summary>
[RequirePermission(AppPermission.UserRolesView)]
public class UserRolesController : Controller
{
    private readonly IdentityServerService _identityServer;
    private readonly ILogger<UserRolesController> _logger;

    public UserRolesController(IdentityServerService identityServer, ILogger<UserRolesController> logger)
    {
        _identityServer = identityServer;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        // SSO: access_token đã hết hạn ⇒ challenge lại OIDC NGAY khi vào trang (thường im lặng vì phiên IdP
        // còn sống) để lấy token mới, tránh vào trang rồi mới thấy mọi thứ rỗng. Ở chế độ Local luôn false
        // ⇒ render bình thường.
        if (await _identityServer.IsSessionExpiredAsync())
            return RedirectToAction("ReAuth", "Account", new { returnUrl = Url.Action(nameof(Index)) });

        return View();
    }

    // Phiên SSO hết hạn giữa chừng (trang mở lâu rồi mới thao tác AJAX): trả tín hiệu authExpired + loginUrl
    // để JS đá người dùng sang endpoint ReAuth (challenge lại OIDC). Trả HTTP 200 để helper fetch phía JS
    // đọc được body thay vì nuốt thành lỗi chung.
    private IActionResult SessionExpiredJson() => Json(new
    {
        ok = false,
        authExpired = true,
        message = "Phiên đăng nhập SSO đã hết hạn. Đang chuyển tới trang đăng nhập lại…",
        loginUrl = Url.Action("ReAuth", "Account", new { returnUrl = Url.Action(nameof(Index)) })
    });

    // Danh sách role của API resource (đổ vào cả dropdown filter lẫn dropdown trong popup Add).
    [HttpGet]
    public async Task<IActionResult> Roles()
    {
        try
        {
            var roles = await _identityServer.GetAllRolesAsync();
            return Json(new { ok = true, items = roles.Select(r => new { id = r.Id, name = r.Name }) });
        }
        catch (IdentityServerSessionExpiredException)
        {
            return SessionExpiredJson();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lấy được danh sách role từ IdentityServer.");
            return Json(new { ok = false, message = "Không kết nối được IdentityServer để lấy danh sách role." });
        }
    }

    // Gợi ý người dùng LDAP cho ô autocomplete. Bỏ qua từ khóa quá ngắn để không spam IS4.
    [HttpGet]
    public async Task<IActionResult> SearchUsers(string searchKey)
    {
        searchKey = searchKey?.Trim() ?? string.Empty;
        if (searchKey.Length < 2)
            return Json(new { ok = true, items = Array.Empty<object>() });

        try
        {
            var users = await _identityServer.SearchLdapUserAsync(searchKey);
            return Json(new { ok = true, items = users.Select(MapUser) });
        }
        catch (IdentityServerSessionExpiredException)
        {
            return SessionExpiredJson();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tra cứu người dùng LDAP thất bại cho từ khóa {SearchKey}.", searchKey);
            return Json(new { ok = false, message = "Không tra cứu được người dùng từ IdentityServer." });
        }
    }

    // Người dùng đang có một role — dùng để hiển thị bảng và cung cấp nút Thu hồi theo hàng.
    [HttpGet]
    public async Task<IActionResult> UsersByRole(string roleName)
    {
        roleName = roleName?.Trim() ?? string.Empty;
        if (roleName.Length == 0)
            return Json(new { ok = true, items = Array.Empty<object>() });

        try
        {
            var users = await _identityServer.GetUsersByRoleAsync(new UserByRoleRequest { RoleKeys = new[] { roleName } });
            return Json(new { ok = true, items = users.Select(MapUser) });
        }
        catch (IdentityServerSessionExpiredException)
        {
            return SessionExpiredJson();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lấy được danh sách người dùng theo role {RoleName}.", roleName);
            return Json(new { ok = false, message = "Không lấy được danh sách người dùng theo role." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.UserRolesManage)]
    public Task<IActionResult> Assign(string roleName, string userName) =>
        AssignOrWithdrawAsync(roleName, userName, assign: true);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.UserRolesManage)]
    public Task<IActionResult> Withdraw(string roleName, string userName) =>
        AssignOrWithdrawAsync(roleName, userName, assign: false);

    private async Task<IActionResult> AssignOrWithdrawAsync(string roleName, string userName, bool assign)
    {
        roleName = roleName?.Trim() ?? string.Empty;
        userName = userName?.Trim() ?? string.Empty;
        if (roleName.Length == 0 || userName.Length == 0)
            return Json(new { ok = false, message = "Vui lòng chọn role và người dùng." });

        var request = new AssignRoleRequest
        {
            ApiResource = _identityServer.ApiName,
            UserName = userName,
            RoleNames = new[] { roleName }
        };

        try
        {
            var success = assign
                ? await _identityServer.AssignRoleAsync(request)
                : await _identityServer.WithdrawRoleAsync(request);

            if (!success)
                return Json(new { ok = false, message = "IdentityServer trả về lỗi khi cập nhật role." });

            return Json(new
            {
                ok = true,
                message = assign
                    ? $"Đã gán role \"{roleName}\" cho {userName}."
                    : $"Đã thu hồi role \"{roleName}\" của {userName}."
            });
        }
        catch (IdentityServerSessionExpiredException)
        {
            return SessionExpiredJson();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cập nhật role {RoleName} cho {UserName} thất bại (assign={Assign}).", roleName, userName, assign);
            return Json(new { ok = false, message = "Không kết nối được IdentityServer để cập nhật role." });
        }
    }

    // Chỉ trả về các trường cần cho UI (không đẩy toàn bộ thông tin LDAP xuống trình duyệt).
    private static object MapUser(LdapUserResponse u) => new
    {
        userName = u.UserName,
        displayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName,
        email = u.Email,
        department = string.IsNullOrWhiteSpace(u.Department) ? u.OrganizationUnit : u.Department
    };
}
