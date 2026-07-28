using ICOGenerator.Application.Projects;
using ICOGenerator.Services.Artifacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Bề mặt dành cho NGƯỜI XEM KHÔNG CÓ TÀI KHOẢN: mở bản demo POC bằng link chia sẻ và ghim góp ý bằng
/// tên mình. Tách hẳn thành controller riêng — thay vì thêm vài action <c>[AllowAnonymous]</c> vào
/// <see cref="ProjectsController"/> — vì đây là toàn bộ phần hệ thống mà người lạ chạm được: gom một chỗ
/// thì đọc một file là thấy hết những gì token mở ra.
///
/// <para>
/// Token chỉ mở đúng ba thứ của MỘT project: trang xem, file poc-demo.html, và danh sách/ghi chú của
/// chính bản demo đó. Không project khác, không tài liệu, không hội thoại, không cấu hình. Mọi action
/// đều đổi token → projectId qua <see cref="ResolvePocShareTokenQuery"/> (hết hạn/thu hồi/không tồn tại
/// đều trả về NotFound giống hệt nhau, nên không dò được token nào từng tồn tại).
/// </para>
/// </summary>
[AllowAnonymous]
[Route("poc-share")]
public class PocShareController : Controller
{
    private readonly ResolvePocShareTokenQuery _resolveToken;
    private readonly GetMockupFileQuery _getMockupFileQuery;
    private readonly GetPocReviewQuery _getPocReviewQuery;
    private readonly ListPocCommentsQuery _listPocCommentsQuery;
    private readonly AddPocCommentUseCase _addPocCommentUseCase;

    public PocShareController(
        ResolvePocShareTokenQuery resolveToken,
        GetMockupFileQuery getMockupFileQuery,
        GetPocReviewQuery getPocReviewQuery,
        ListPocCommentsQuery listPocCommentsQuery,
        AddPocCommentUseCase addPocCommentUseCase)
    {
        _resolveToken = resolveToken;
        _getMockupFileQuery = getMockupFileQuery;
        _getPocReviewQuery = getPocReviewQuery;
        _listPocCommentsQuery = listPocCommentsQuery;
        _addPocCommentUseCase = addPocCommentUseCase;
    }

    /// <summary>
    /// Trang xem của khách: bản demo trong iframe + kịch bản nghiệm thu + khung ghi chú. Tên action là
    /// <c>Open</c> chứ không phải <c>View</c> để không che mất <see cref="Controller.View(object)"/>.
    /// </summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Open(string token)
    {
        var projectId = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (projectId == null)
            return NotFound("Link chia sẻ không còn hiệu lực.");

        var page = await _getPocReviewQuery.ExecuteAsync(projectId.Value, HttpContext.RequestAborted);
        if (page is not { HasMockup: true })
            return NotFound("Dự án này chưa có bản demo để xem.");

        ViewBag.ShareToken = token;
        return View(page);
    }

    /// <summary>
    /// File poc-demo.html phục vụ cho iframe của khách. Giữ NGUYÊN hai lớp bảo vệ của đường có đăng nhập:
    /// cắt khối hướng dẫn dành cho agent, và CSP sandbox không có <c>allow-same-origin</c> — HTML này do
    /// LLM sinh ra nên phải chạy ở origin mờ, kể cả khi người xem là khách.
    /// </summary>
    [HttpGet("{token}/demo")]
    public async Task<IActionResult> Demo(string token, bool review = false)
    {
        var projectId = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (projectId == null)
            return NotFound("Link chia sẻ không còn hiệu lực.");

        var result = await _getMockupFileQuery.ExecuteAsync(projectId.Value);
        if (result == null)
            return NotFound("Chưa có bản demo.");

        var html = await System.IO.File.ReadAllTextAsync(result.FilePath, HttpContext.RequestAborted);
        html = PocTemplate.StripDeveloperGuide(html);
        if (review)
            html = PocTemplate.InjectAnnotator(html);

        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-forms allow-modals;";
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet("{token}/comments")]
    public async Task<IActionResult> Comments(string token)
    {
        var projectId = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (projectId == null)
            return NotFound();

        // Khách KHÔNG xóa được ghi chú của ai (kể cả của chính mình — không có danh tính để chứng minh),
        // nên canManage/currentUsername đều rỗng: mọi mục về với CanDelete = false.
        return Json(await _listPocCommentsQuery.ExecuteAsync(projectId.Value, currentUsername: null, canManage: false, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Khách ghim một góp ý. Tên người góp ý được gắn tiền tố "Khách ·" để trong danh sách không thể lẫn
    /// với tài khoản thật — người nhận góp ý cần biết ngay dòng này đến từ link chia sẻ.
    /// </summary>
    [HttpPost("{token}/comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(
        string token, string? guestName, string? pageView, string? elementLabel, string? elementPath,
        double xPercent, double yPercent, string? comment)
    {
        var projectId = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (projectId == null)
            return NotFound("Link chia sẻ không còn hiệu lực.");

        var name = (guestName ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest("Nhập tên của bạn để đội dự án biết góp ý này của ai.");
        if (name.Length > 60)
            name = name[..60];

        var (result, item) = await _addPocCommentUseCase.ExecuteAsync(
            projectId.Value, pageView, elementLabel, elementPath, xPercent, yPercent, comment,
            $"Khách · {name}", HttpContext.RequestAborted);

        return result switch
        {
            AddPocCommentResult.Ok => Json(item),
            AddPocCommentResult.MissingComment => BadRequest("Nội dung góp ý trống."),
            AddPocCommentResult.TooManyComments => BadRequest("Bản demo này đã có quá nhiều góp ý — báo đội dự án xử lý bớt trước nhé."),
            _ => NotFound("Không tìm thấy bản demo.")
        };
    }
}
