using ICOGenerator.Application.Requirements;
using ICOGenerator.Services.Requirements;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class CreateDocumentController : Controller
{
    private readonly GetRequirementWorkspaceQuery _getRequirementWorkspaceQuery;
    private readonly GenerateRequirementDraftUseCase _generateRequirementDraftUseCase;
    private readonly ApproveRequirementUseCase _approveRequirementUseCase;
    private readonly StartRequirementChatUseCase _startRequirementChatUseCase;
    private readonly GetRequirementJobStatusQuery _getRequirementJobStatusQuery;
    private readonly GetDocumentDownloadQuery _getDocumentDownloadQuery;

    public CreateDocumentController(
       GetRequirementWorkspaceQuery getRequirementWorkspaceQuery,
       GenerateRequirementDraftUseCase generateRequirementDraftUseCase,
       ApproveRequirementUseCase approveRequirementUseCase,
       StartRequirementChatUseCase startRequirementChatUseCase,
       GetRequirementJobStatusQuery getRequirementJobStatusQuery,
       GetDocumentDownloadQuery getDocumentDownloadQuery)
    {
        _getRequirementWorkspaceQuery = getRequirementWorkspaceQuery;
        _generateRequirementDraftUseCase = generateRequirementDraftUseCase;
        _approveRequirementUseCase = approveRequirementUseCase;
        _startRequirementChatUseCase = startRequirementChatUseCase;
        _getRequirementJobStatusQuery = getRequirementJobStatusQuery;
        _getDocumentDownloadQuery = getDocumentDownloadQuery;
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
            await _generateRequirementDraftUseCase.ExecuteAsync(projectId, message);

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid projectId)
    {
        var result = await _approveRequirementUseCase.ExecuteAsync(projectId);

        if (result == ApproveRequirementResult.MissingAiDesignSpec)
        {
            TempData["Error"] = "AI Design Spec chưa được tạo. Vui lòng chat với BA trước khi approve.";
            return RedirectToAction(nameof(Index), new { projectId });
        }

        if (result == ApproveRequirementResult.NoDraftDocuments)
            return RedirectToAction(nameof(Index), new { projectId });

        return RedirectToAction("Index", "ManageAgent", new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult NewChat(Guid projectId)
    {
        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpGet]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var result = await _getDocumentDownloadQuery.ExecuteAsync(id);
        if (result == null)
            return NotFound("Document not found.");

        return PhysicalFile(result.FilePath, result.ContentType, result.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartChat(Guid projectId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return BadRequest();

        var jobId = await _startRequirementChatUseCase.ExecuteAsync(projectId, message);
        return Json(new { jobId });
    }

    [HttpGet]
    public async Task<IActionResult> JobStatus(Guid jobId)
    {
        var status = await _getRequirementJobStatusQuery.ExecuteAsync(jobId);
        if (status == null)
            return NotFound();

        return Json(new
        {
            status.Id,
            Status = status.Status.ToString(),
            status.CurrentStep,
            status.Error
        });
    }
}
