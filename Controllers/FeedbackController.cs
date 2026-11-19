using ICOGenerator.Localization;
using ICOGenerator.Application.Feedback;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Feedback;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Mặc định cả controller chỉ cần quyền FeedbackView (gửi + xem phản hồi của mình). Các thao tác triage
// (đổi trạng thái) yêu cầu thêm FeedbackManage. Xem/tải file và xóa được kiểm soát thêm ở tầng use case
// (chủ sở hữu phản hồi hoặc người có FeedbackManage).
[RequirePermission(AppPermission.FeedbackView)]
public class FeedbackController : Controller
{
    private readonly GetFeedbackPageQuery _getFeedbackPageQuery;
    private readonly SubmitFeedbackUseCase _submitFeedbackUseCase;
    private readonly UpdateFeedbackStatusUseCase _updateFeedbackStatusUseCase;
    private readonly GetFeedbackAttachmentQuery _getFeedbackAttachmentQuery;
    private readonly DeleteFeedbackUseCase _deleteFeedbackUseCase;
    private readonly IPermissionService _permissions;

    public FeedbackController(
        GetFeedbackPageQuery getFeedbackPageQuery,
        SubmitFeedbackUseCase submitFeedbackUseCase,
        UpdateFeedbackStatusUseCase updateFeedbackStatusUseCase,
        GetFeedbackAttachmentQuery getFeedbackAttachmentQuery,
        DeleteFeedbackUseCase deleteFeedbackUseCase,
        IPermissionService permissions)
    {
        _getFeedbackPageQuery = getFeedbackPageQuery;
        _submitFeedbackUseCase = submitFeedbackUseCase;
        _updateFeedbackStatusUseCase = updateFeedbackStatusUseCase;
        _getFeedbackAttachmentQuery = getFeedbackAttachmentQuery;
        _deleteFeedbackUseCase = deleteFeedbackUseCase;
        _permissions = permissions;
    }

    public async Task<IActionResult> Index(FeedbackStatus? status = null, FeedbackType? type = null)
    {
        var canManage = await _permissions.HasPermissionAsync(User, AppPermission.FeedbackManage, HttpContext.RequestAborted);
        var page = await _getFeedbackPageQuery.ExecuteAsync(User.Identity?.Name, canManage, status, type, HttpContext.RequestAborted);
        return View(page);
    }

    // Gửi phản hồi (kèm file tuỳ chọn). Nâng trần kích thước request để cho phép đính kèm video/tài liệu lớn:
    // mặc định Kestrel ~28MB và multipart 128MB là quá nhỏ. Giới hạn từng-file/số-lượng thật do
    // FeedbackAttachmentStore áp (hằng số 50MB/file, 8 file).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(524_288_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 524_288_000)]
    public async Task<IActionResult> Submit(SubmitFeedbackVm input, List<IFormFile> files)
    {
        try
        {
            await _submitFeedbackUseCase.ExecuteAsync(input, files, User.Identity?.Name, User.Identity?.Name, HttpContext.RequestAborted);
            TempData["Success"] = Loc.T("FeedbackController.Submit");
        }
        catch (FeedbackValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.FeedbackManage)]
    public async Task<IActionResult> UpdateStatus(Guid id, FeedbackStatus status)
    {
        var result = await _updateFeedbackStatusUseCase.ExecuteAsync(id, status, HttpContext.RequestAborted);
        if (result == UpdateFeedbackStatusResult.NotFound)
            TempData["Error"] = Loc.T("FeedbackController.UpdateStatus");
        else
            TempData["Success"] = Loc.T("FeedbackController.UpdateStatus.2");

        return RedirectToAction(nameof(Index));
    }

    // Xem (inline) hoặc tải (download=true) một file đính kèm. Quyền truy cập do use case kiểm: chủ phản hồi
    // hoặc người có FeedbackManage; không đủ quyền/không tồn tại ⇒ 404.
    [HttpGet]
    public async Task<IActionResult> Attachment(Guid id, bool download = false)
    {
        var canManage = await _permissions.HasPermissionAsync(User, AppPermission.FeedbackManage, HttpContext.RequestAborted);
        var file = await _getFeedbackAttachmentQuery.ExecuteAsync(id, User.Identity?.Name, canManage, HttpContext.RequestAborted);
        if (file == null)
            return NotFound();

        // download=true ⇒ đính kèm tên file (Content-Disposition: attachment); ngược lại trả inline để xem
        // ảnh/video ngay trên trang. enableRangeProcessing cho phép tua video (range request).
        return download
            ? PhysicalFile(file.StoredPath, file.ContentType, file.FileName)
            : PhysicalFile(file.StoredPath, file.ContentType, enableRangeProcessing: true);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var canManage = await _permissions.HasPermissionAsync(User, AppPermission.FeedbackManage, HttpContext.RequestAborted);
        var result = await _deleteFeedbackUseCase.ExecuteAsync(id, User.Identity?.Name, canManage, HttpContext.RequestAborted);

        TempData[result == DeleteFeedbackResult.Ok ? "Success" : "Error"] = result switch
        {
            DeleteFeedbackResult.Ok => Loc.T("FeedbackController.Delete"),
            DeleteFeedbackResult.NotFound => Loc.T("FeedbackController.Delete.2"),
            _ => Loc.T("FeedbackController.Delete.3"),
        };

        return RedirectToAction(nameof(Index));
    }
}
