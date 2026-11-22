using ICOGenerator.Application.Audit;
using ICOGenerator.Application.SystemLogs;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Màn Logs: một mục menu, hai tab — Audit Log (thay đổi cấu hình) và System Logs (log Warning+ đã chép vào DB).
// Mỗi tab là MỘT action riêng giữ nguyên quyền riêng của nó: SystemLogView tách khỏi AuditView vì exception và
// thuộc tính log có thể lộ đường dẫn máy chủ, tên bảng, trích dữ liệu người dùng — gộp màn không được gộp quyền.
// Cổng cấp controller chỉ cần một trong hai quyền; tab thiếu quyền bị ẩn khỏi dải tab và action của nó trả 403.
[RequirePermission(AppPermission.AuditView, AppPermission.SystemLogView)]
public class LogsController : Controller
{
    private readonly GetAuditLogPageQuery _getAuditLogPageQuery;
    private readonly GetSystemLogPageQuery _getSystemLogPageQuery;
    private readonly IPermissionService _permissions;

    public LogsController(
        GetAuditLogPageQuery getAuditLogPageQuery,
        GetSystemLogPageQuery getSystemLogPageQuery,
        IPermissionService permissions)
    {
        _getAuditLogPageQuery = getAuditLogPageQuery;
        _getSystemLogPageQuery = getSystemLogPageQuery;
        _permissions = permissions;
    }

    // /Logs ⇒ tab đầu tiên người dùng được xem (Audit Log trước, không có thì System Logs).
    public async Task<IActionResult> Index()
        => await _permissions.HasPermissionAsync(User, AppPermission.AuditView, HttpContext.RequestAborted)
            ? RedirectToAction(nameof(Audit))
            : RedirectToAction(nameof(SystemLogs));

    [RequirePermission(AppPermission.AuditView)]
    public async Task<IActionResult> Audit(
        AuditCategory? category = null, int page = 1, int pageSize = GetAuditLogPageQuery.DefaultPageSize)
    {
        return View(await _getAuditLogPageQuery.ExecuteAsync(category, page, pageSize, HttpContext.RequestAborted));
    }

    [RequirePermission(AppPermission.SystemLogView)]
    public async Task<IActionResult> SystemLogs(
        SystemLogLevel? level = null, string? q = null, int page = 1, int pageSize = GetSystemLogPageQuery.DefaultPageSize)
    {
        return View(await _getSystemLogPageQuery.ExecuteAsync(level, q, page, pageSize, HttpContext.RequestAborted));
    }
}
