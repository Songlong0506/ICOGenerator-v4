using System.Text.Json;
using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Mặc định cả controller chỉ cần quyền xem; các action thay đổi dữ liệu/workflow yêu cầu RequirementsManage.
[RequirePermission(AppPermission.RequirementsView)]
public class RequirementsController : Controller
{
    private readonly GetRequirementWorkspaceQuery _getRequirementWorkspaceQuery;
    private readonly GenerateRequirementDraftUseCase _generateRequirementDraftUseCase;
    private readonly ChatWithBAUseCase _chatWithBAUseCase;
    private readonly ApproveRequirementUseCase _approveRequirementUseCase;
    private readonly GetDocumentDownloadQuery _getDocumentDownloadQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly StreamWorkflowProgressQuery _streamWorkflowProgressQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;
    private readonly StartNewChatUseCase _startNewChatUseCase;
    private readonly UploadProjectSourceUseCase _uploadProjectSourceUseCase;
    private readonly DeleteProjectSourceUseCase _deleteProjectSourceUseCase;

    // SSE frames are hand-serialized, so match the camelCase the polling JSON (and client) already use.
    private static readonly JsonSerializerOptions SseJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RequirementsController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ChatWithBAUseCase chatWithBAUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       GetDocumentDownloadQuery getDocumentDownloadQuery,
       GetWorkflowStatusQuery getWorkflowStatusQuery,
       StreamWorkflowProgressQuery streamWorkflowProgressQuery,
       GetDocumentPreviewQuery getDocumentPreviewQuery,
       StartNewChatUseCase startNewChatUseCase,
       UploadProjectSourceUseCase uploadProjectSourceUseCase,
       DeleteProjectSourceUseCase deleteProjectSourceUseCase)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _streamWorkflowProgressQuery = streamWorkflowProgressQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
        _startNewChatUseCase = startNewChatUseCase;
        _uploadProjectSourceUseCase = uploadProjectSourceUseCase;
        _deleteProjectSourceUseCase = deleteProjectSourceUseCase;
    }

    public async Task<IActionResult> Index(Guid projectId, string? version = null)
    {
        var result = await _getRequirementWorkspaceQuery.ExecuteAsync(projectId, version);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.SelectedVersion = result.SelectedVersion;
        ViewBag.BaSupportsVision = result.BaModelSupportsVision;
        return View(result.Project);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> Chat(Guid projectId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return RedirectToAction(nameof(Index), new { projectId });

        try
        {
            var result = await _chatWithBAUseCase.ExecuteAsync(projectId, message);

            if (result == ChatWithBAResult.ProjectNotFound)
                return RedirectToAction("Index", "Projects");

            if (result == ChatWithBAResult.BaNotConfigured)
                TempData["Error"] = "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst). Hãy tạo/kích hoạt agent BA và gán AI model trong màn hình Manage Agent.";
        }
        catch (BudgetExceededException ex)
        {
            // Đã chạm trần ngân sách: đừng để văng thành lỗi 500 — báo lý do để người dùng biết vì sao BA không trả lời.
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Upload tài liệu nguồn (ảnh/PDF) cho project. Nâng trần kích thước request để cho phép vài file ảnh/PDF
    // (mặc định Kestrel ~28MB; multipart 128MB) — đặt 60MB cho cả request lẫn multipart body.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequestSizeLimit(60_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60_000_000)]
    public async Task<IActionResult> UploadSource(Guid projectId, List<IFormFile> files)
    {
        try
        {
            var result = await _uploadProjectSourceUseCase.ExecuteAsync(projectId, files, User.Identity?.Name);

            if (result == UploadProjectSourceResult.ProjectNotFound)
                return RedirectToAction("Index", "Projects");
            if (result == UploadProjectSourceResult.NoFiles)
                TempData["Error"] = "Chưa chọn file nào để upload.";
            else
                TempData["SourceUploaded"] = true;
        }
        catch (SourceFileValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> DeleteSource(Guid id, Guid projectId)
    {
        await _deleteProjectSourceUseCase.ExecuteAsync(id);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> WriteRequirement(Guid projectId)
    {
        await _generateRequirementDraftUseCase.ExecuteAsync(projectId);
        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> Approve(Guid projectId)
    {
        var result = await _approveRequirementUseCase.ExecuteAsync(projectId);

        if (result == ApproveRequirementResult.ProjectNotFound)
            return RedirectToAction("Index", "Projects");

        if (result == ApproveRequirementResult.MissingProductBrief)
        {
            TempData["Error"] = "Product Brief chưa được tạo. Vui lòng bấm \"Write Requirement\" để tạo Product Brief trước khi approve.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        if (result == ApproveRequirementResult.NoDraftDocuments)
            return RedirectToAction(nameof(Index), new { projectId });

        if (result == ApproveRequirementResult.PromotionFailed)
        {
            TempData["Error"] = "Không thể chuyển tài liệu draft sang phiên bản đã duyệt (file có thể đang bị mở/khóa). Đóng file đang mở rồi thử lại.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        if (result == ApproveRequirementResult.WorkflowStartFailed)
        {
            TempData["Error"] = "Tài liệu đã được duyệt nhưng không khởi động được workflow sinh AI Design Spec / tạo POC. Vui lòng thử lại.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Cổng duyệt/đẩy bước delivery (ApproveStage/RejectStage/RetryWorkflow) đã chuyển sang
    // AgentDashboardController và yêu cầu quyền DeliveryAdvance: user thường dừng ở bước POC,
    // chỉ TeamDev/Admin mới đẩy tiếp các bước Architecture/code/test trên Agent Dashboard.

    [HttpGet]
    public async Task<IActionResult> WorkflowStatus(Guid projectId, Guid? runId = null, long afterSeq = 0)
    {
        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId, runId, afterSeq));
    }

    // Server-Sent Events: đẩy realtime tiến độ + token "suy nghĩ" của agent cho một run, thay vì để
    // trình duyệt poll mỗi 1.5s. Trả về Task (ghi thẳng vào Response body) đúng giao thức text/event-stream.
    [HttpGet]
    public async Task WorkflowStream(Guid projectId, Guid runId, long afterSeq = 0)
    {
        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        // Không set header "Connection" tay: nó là reserved header, set sẽ ném lỗi dưới HTTP/2 (mặc định của Kestrel khi HTTPS).
        // Tắt buffering (cả của Kestrel lẫn reverse-proxy như nginx) để mỗi frame tới ngay browser.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            await foreach (var ev in _streamWorkflowProgressQuery.ExecuteAsync(projectId, runId, afterSeq, cancellationToken))
            {
                var frame = ev is null
                    ? ": ping\n\n"
                    : $"data: {JsonSerializer.Serialize(ev, SseJsonOptions)}\n\n";

                await Response.WriteAsync(frame, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            // Báo client đóng kết nối thay vì để EventSource tự reconnect (run đã kết thúc, không còn gì để stream).
            await Response.WriteAsync("event: end\ndata: {}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client đã rời trang (RequestAborted): kết thúc êm, không phải lỗi.
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> NewChat(Guid projectId)
    {
        await _startNewChatUseCase.ExecuteAsync(projectId);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpGet]
    public async Task<IActionResult> DocumentPreview(Guid id)
    {
        var result = await _getDocumentPreviewQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var result = await _getDocumentDownloadQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return PhysicalFile(result.FilePath, result.ContentType, result.FileName);
    }
}
