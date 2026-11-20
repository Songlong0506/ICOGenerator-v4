using ICOGenerator.Application.SystemLogs;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Log ứng dụng (Warning+) đã chép vào DB — chỉ để xem. Gác sau quyền riêng SystemLogView vì exception và
// thuộc tính log có thể lộ đường dẫn máy chủ, tên bảng, trích dữ liệu người dùng.
[RequirePermission(AppPermission.SystemLogView)]
public class SystemLogsController : Controller
{
    private readonly GetSystemLogPageQuery _getSystemLogPageQuery;

    public SystemLogsController(GetSystemLogPageQuery getSystemLogPageQuery)
        => _getSystemLogPageQuery = getSystemLogPageQuery;

    public async Task<IActionResult> Index(
        SystemLogLevel? level = null, string? q = null, int page = 1, int pageSize = GetSystemLogPageQuery.DefaultPageSize)
    {
        return View(await _getSystemLogPageQuery.ExecuteAsync(level, q, page, pageSize, HttpContext.RequestAborted));
    }
}
