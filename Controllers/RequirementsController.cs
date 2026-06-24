using System.Text.Json;
using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
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
    private readonly ApproveStageUseCase _approveStageUseCase;
    private readonly RejectStageUseCase _rejectStageUseCase;
    private readonly RetryWorkflowUseCase _retryWorkflowUseCase;
    private readonly GetDocumentDownloadQuery _getDocumentDownloadQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly StreamWorkflowProgressQuery _streamWorkflowProgressQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;
    private readonly StartNewChatUseCase _startNewChatUseCase;

    // SSE frames are hand-serialized, so match the camelCase the polling JSON (and client) already use.
    private static readonly JsonSerializerOptions SseJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RequirementsController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ChatWithBAUseCase chatWithBAUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       ApproveStageUseCase approveStageUseCase,
       RejectStageUseCase rejectStageUseCase,
       RetryWorkflowUseCase retryWorkflowUseCase,
       GetDocumentDownloadQuery getDocumentDownloadQuery,
       GetWorkflowStatusQuery getWorkflowStatusQuery,
       StreamWorkflowProgressQuery streamWorkflowProgressQuery,
       GetDocumentPreviewQuery getDocumentPreviewQuery,
       StartNewChatUseCase startNewChatUseCase)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _approveStageUseCase = approveStageUseCase;
        _rejectStageUseCase = rejectStageUseCase;
        _retryWorkflowUseCase = retryWorkflowUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _streamWorkflowProgressQuery = streamWorkflowProgressQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
        _startNewChatUseCase = startNewChatUseCase;
    }

    public async Task<IActionResult> Index(Guid projectId, string? version = null)
    {
        var result = await _getRequirementWorkspaceQuery.ExecuteAsync(projectId, version);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.SelectedVersion = result.SelectedVersion;
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

        if (result == ApproveRequirementResult.MissingAiDesignSpec)
        {
            TempData["Error"] = "AI Design Spec chưa được tạo. Vui lòng chat với BA trước khi approve.";
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
            TempData["Error"] = "Tài liệu đã được duyệt nhưng không khởi động được workflow tạo POC. Vui lòng thử lại.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> ApproveStage(Guid projectId, Guid? runId = null)
    {
        var result = await _approveStageUseCase.ExecuteAsync(projectId, runId);

        if (result == ApproveStageResult.MissingAgent)
            TempData["Error"] = "Không tìm thấy agent cho bước kế tiếp. Hãy kiểm tra cấu hình agent.";

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> RejectStage(Guid projectId, Guid? runId = null)
    {
        await _rejectStageUseCase.ExecuteAsync(projectId, runId);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Chạy lại bước đã thất bại (vd POC) mà không Approve lại từ đầu — dùng khi lỗi tạm thời như
    // LLM rớt kết nối. Re-queue đúng task đã hỏng, worker sẽ tiếp tục từ chỗ đó.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    public async Task<IActionResult> RetryWorkflow(Guid projectId, Guid? runId = null)
    {
        var result = await _retryWorkflowUseCase.ExecuteAsync(projectId, runId);

        if (result == RetryWorkflowResult.NoFailedRun || result == RetryWorkflowResult.NoRetryableTask)
            TempData["Error"] = "Không tìm thấy bước thất bại nào để chạy lại.";

        return RedirectToAction(nameof(Index), new { projectId });
    }

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
