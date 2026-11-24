using ICOGenerator.Localization;
using ICOGenerator.Application.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Bề mặt dành cho NGƯỜI XEM KHÔNG CÓ TÀI KHOẢN: mở bản demo POC bằng link chia sẻ và ghim góp ý — góp
/// ý đứng tên người nhận link ("Send to" lúc tạo link), khách không phải tự khai tên. Tách hẳn thành controller riêng — thay vì thêm vài action <c>[AllowAnonymous]</c> vào
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
        var access = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (access == null)
            return NotFound(Loc.T("PocShareController.Open"));

        var page = await _getPocReviewQuery.ExecuteAsync(access.ProjectId, HttpContext.RequestAborted);
        if (page is not { HasMockup: true })
            return NotFound(Loc.T("PocShareController.Open.2"));

        ViewBag.ShareToken = token;
        ViewBag.ShareRecipient = access.RecipientName;
        return View(page);
    }

    /// <summary>
    /// File poc-demo.html phục vụ cho iframe của khách. Dùng CHUNG <see cref="PocDemoResponse"/> với đường
    /// có đăng nhập, nên khách và người đã đăng nhập không thể nhận hai mức rào chắn khác nhau.
    /// </summary>
    [HttpGet("{token}/demo")]
    public async Task<IActionResult> Demo(string token, bool review = false)
    {
        var access = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (access == null)
            return NotFound(Loc.T("PocShareController.Demo"));

        var result = await _getMockupFileQuery.ExecuteAsync(access.ProjectId);
        if (result == null)
            return NotFound(Loc.T("PocShareController.Demo.2"));

        return await PocDemoResponse.BuildAsync(this, result.FilePath, review, HttpContext.RequestAborted);
    }

    [HttpGet("{token}/comments")]
    public async Task<IActionResult> Comments(string token)
    {
        var access = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (access == null)
            return NotFound();

        // Khách KHÔNG xóa được ghi chú của ai (kể cả của chính mình — không có danh tính để chứng minh),
        // nên canManage/currentUsername đều rỗng: mọi mục về với CanWithdraw = false. Không có ghi chú Brief:
        // khách chỉ thấy góp ý trên bản demo, không thấy tài liệu nội bộ.
        return Json(await _listPocCommentsQuery.ExecuteAsync(access.ProjectId, currentUsername: null, canManage: false, includeBriefNotes: false, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Khách ghim một góp ý. Người góp ý là NGƯỜI NHẬN LINK ("Send to" lúc tạo link) — mỗi link là của một
    /// người, nên tên lấy từ link chứ không tin vào ô khách tự gõ. <paramref name="guestName"/> chỉ còn dùng
    /// cho link cũ tạo khi "Send to" chưa bắt buộc. Tên được gắn tiền tố "Khách ·" để trong danh sách không
    /// thể lẫn với tài khoản thật — người nhận góp ý cần biết ngay dòng này đến từ link chia sẻ.
    /// </summary>
    [HttpPost("{token}/comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(
        string token, string? guestName, string? pageView, string? elementLabel, string? elementPath,
        double xPercent, double yPercent, string? comment)
    {
        var access = await _resolveToken.ExecuteAsync(token, HttpContext.RequestAborted);
        if (access == null)
            return NotFound(Loc.T("PocShareController.AddComment"));

        var name = (access.RecipientName ?? guestName ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(Loc.T("PocShareController.AddComment.2"));
        // CreatedByUsername dài tối đa 100, trừ chỗ cho tiền tố "Khách · ".
        if (name.Length > 90)
            name = name[..90];

        var (result, item) = await _addPocCommentUseCase.ExecuteAsync(
            access.ProjectId, pageView, elementLabel, elementPath, xPercent, yPercent, comment,
            Loc.T("PocShareController.AddComment.3", name), HttpContext.RequestAborted);

        return result switch
        {
            AddPocCommentResult.Ok => Json(item),
            AddPocCommentResult.MissingComment => BadRequest(Loc.T("PocShareController.AddComment.4")),
            AddPocCommentResult.TooManyComments => BadRequest(Loc.T("PocShareController.AddComment.5")),
            // Khách không thấy nút mở khoá (nó nằm trong trang review có đăng nhập), nên câu này nói theo
            // ngôn ngữ của họ: đã chốt rồi, muốn góp thêm thì báo đội dự án.
            AddPocCommentResult.PocAccepted => Conflict(Loc.T("PocShareController.AddComment.6")),
            _ => NotFound(Loc.T("PocShareController.AddComment.7"))
        };
    }
}
