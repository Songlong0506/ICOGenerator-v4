using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Services.Requirements;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class RequirementsController : Controller
{
    private readonly GetRequirementWorkspaceQuery _getRequirementWorkspaceQuery;
    private readonly GenerateRequirementDraftUseCase _generateRequirementDraftUseCase;
    private readonly ChatWithBAUseCase _chatWithBAUseCase;
    private readonly ApproveRequirementUseCase _approveRequirementUseCase;
    private readonly GetDocumentDownloadQuery _getDocumentDownloadQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;
    private readonly StartNewChatUseCase _startNewChatUseCase;

    public RequirementsController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ChatWithBAUseCase chatWithBAUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       GetDocumentDownloadQuery getDocumentDownloadQuery,
       GetWorkflowStatusQuery getWorkflowStatusQuery,
       GetDocumentPreviewQuery getDocumentPreviewQuery,
       StartNewChatUseCase startNewChatUseCase)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
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
    public async Task<IActionResult> Chat(Guid projectId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return RedirectToAction(nameof(Index), new { projectId });

        var result = await _chatWithBAUseCase.ExecuteAsync(projectId, message);

        if (result == ChatWithBAResult.ProjectNotFound)
            return RedirectToAction("Index", "Projects");

        if (result == ChatWithBAResult.BaNotConfigured)
            TempData["Error"] = "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst). Hãy tạo/kích hoạt agent BA và gán AI model trong màn hình Manage Agent.";

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WriteRequirement(Guid projectId)
    {
        await _generateRequirementDraftUseCase.ExecuteAsync(projectId);
        TempData["WorkflowStarted"] = true;
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
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

    [HttpGet]
    public async Task<IActionResult> WorkflowStatus(Guid projectId, Guid? runId = null, long afterSeq = 0)
    {
        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId, runId, afterSeq));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
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
