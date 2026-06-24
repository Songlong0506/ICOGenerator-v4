using ICOGenerator.Application.Roles;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Toàn bộ màn hình cấu hình quyền chỉ dành cho người có quyền quản trị (mặc định chỉ Admin).
[RequirePermission(AppPermission.AdministrationManageRoles)]
public class RolesController : Controller
{
    private readonly GetRolePermissionMatrixQuery _getRolePermissionMatrixQuery;
    private readonly UpdateRolePermissionsUseCase _updateRolePermissionsUseCase;

    public RolesController(
        GetRolePermissionMatrixQuery getRolePermissionMatrixQuery,
        UpdateRolePermissionsUseCase updateRolePermissionsUseCase)
    {
        _getRolePermissionMatrixQuery = getRolePermissionMatrixQuery;
        _updateRolePermissionsUseCase = updateRolePermissionsUseCase;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getRolePermissionMatrixQuery.ExecuteAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string[]? granted)
    {
        await _updateRolePermissionsUseCase.ExecuteAsync(granted);
        TempData["Success"] = "Đã lưu cấu hình quyền. Thay đổi có hiệu lực ngay, không cần đăng nhập lại.";
        return RedirectToAction(nameof(Index));
    }
}
