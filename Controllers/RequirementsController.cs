using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
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

    public RequirementsController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ChatWithBAUseCase chatWithBAUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       GetDocumentDownloadQuery getDocumentDownloadQuery,
       GetWorkflowStatusQuery getWorkflowStatusQuery,
       GetDocumentPreviewQuery getDocumentPreviewQuery)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _chatWithBAUseCase = chatWithBAUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
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
        if (!string.IsNullOrWhiteSpace(message))
            await _chatWithBAUseCase.ExecuteAsync(projectId, message);

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
    public IActionResult NewChat(Guid projectId)
    {
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
