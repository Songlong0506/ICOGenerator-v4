using ICOGenerator.Application.Audit;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Nhật ký thay đổi cấu hình chỉ để xem; gác sau quyền AuditView (Admin có ngầm định, TeamDev được seed).
[RequirePermission(AppPermission.AuditView)]
public class AuditController : Controller
{
    private readonly GetAuditLogPageQuery _getAuditLogPageQuery;

    public AuditController(GetAuditLogPageQuery getAuditLogPageQuery)
        => _getAuditLogPageQuery = getAuditLogPageQuery;

    public async Task<IActionResult> Index(
        AuditCategory? category = null, int page = 1, int pageSize = GetAuditLogPageQuery.DefaultPageSize)
    {
        return View(await _getAuditLogPageQuery.ExecuteAsync(category, page, pageSize, HttpContext.RequestAborted));
    }
}
