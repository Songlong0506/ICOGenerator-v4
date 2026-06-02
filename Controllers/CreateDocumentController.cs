using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Requirements;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

public class CreateDocumentController : Controller
{
    private readonly AppDbContext _db;
    private readonly BARequirementService _baRequirementService;
    private readonly ApproveRequirementUseCase _approveRequirementUseCase;

    public CreateDocumentController(
       AppDbContext db,
       BARequirementService baRequirementService,
       ApproveRequirementUseCase approveRequirementUseCase)
    {
        _db = db;
        _baRequirementService = baRequirementService;
        _approveRequirementUseCase = approveRequirementUseCase;
    }

    public async Task<IActionResult> Index(Guid projectId, string? version = null)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations.OrderBy(c => c.CreatedAt))
                .ThenInclude(x => x.Agent)
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return RedirectToAction("Index", "Projects");

        var selectedVersion = version;

        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            selectedVersion = project.Documents
                .Any(x => x.VersionName == "draft")
                    ? "draft"
                    : project.Documents
                        .Where(x => x.VersionName.StartsWith("V"))
                        .OrderByDescending(x =>
                            int.TryParse(x.VersionName.Replace("V", ""), out var n) ? n : 0)
                        .Select(x => x.VersionName)
                        .FirstOrDefault();
        }

        ViewBag.SelectedVersion = selectedVersion ?? "draft";

        return View(project);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat(Guid projectId, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            await _baRequirementService.GenerateOrUpdateDraftAsync(projectId, message);

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
        var doc = await _db.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (doc == null)
            return NotFound("Document not found.");

        if (string.IsNullOrWhiteSpace(doc.FilePath))
            return NotFound("Document FilePath is empty.");

        var filePath = doc.FilePath;

        if (!System.IO.File.Exists(filePath))
            return NotFound($"File not found: {filePath}");

        var contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        return PhysicalFile(filePath, contentType, doc.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartChat(Guid projectId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return BadRequest();

        var ba = await _db.Agents.FirstAsync(x => x.Name == "BA");

        var job = new AgentJob
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            UserMessage = message,
            Status = "Queued",
            CurrentStep = "Queued..."
        };

        _db.AgentJobs.Add(job);
        await _db.SaveChangesAsync();

        return Json(new
        {
            jobId = job.Id
        });
    }

    [HttpGet]
    public async Task<IActionResult> JobStatus(Guid jobId)
    {
        var job = await _db.AgentJobs.FirstOrDefaultAsync(x => x.Id == jobId);

        if (job == null)
            return NotFound();

        return Json(new
        {
            job.Id,
            job.Status,
            job.CurrentStep,
            job.Error
        });
    }

}
