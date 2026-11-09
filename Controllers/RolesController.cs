using ICOGenerator.Application.Roles;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Ma trận Roles & Permissions không còn là màn hình riêng: nó là popup "Permissions" trên trang
// User Roles (ma trận do UserRolesController.Index nạp sẵn). Controller này chỉ giữ endpoint lưu và
// một lối chuyển hướng cho link/bookmark cũ /Roles. Mặc định chỉ Admin có quyền.
[RequirePermission(AppPermission.AdministrationManageRoles)]
public class RolesController : Controller
{
    private readonly UpdateRolePermissionsUseCase _updateRolePermissionsUseCase;

    public RolesController(UpdateRolePermissionsUseCase updateRolePermissionsUseCase)
    {
        _updateRolePermissionsUseCase = updateRolePermissionsUseCase;
    }

    // /Roles cũ ⇒ trang User Roles, mở sẵn popup Permissions (user-roles.js đọc hash #permissions).
    public IActionResult Index() =>
        Redirect(Url.Action(nameof(UserRolesController.Index), "UserRoles") + "#permissions");

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string[]? granted)
    {
        await _updateRolePermissionsUseCase.ExecuteAsync(granted);
        TempData["Success"] = "Đã lưu cấu hình quyền. Thay đổi có hiệu lực ngay, không cần đăng nhập lại.";
        return RedirectToAction(nameof(UserRolesController.Index), "UserRoles");
    }
}
