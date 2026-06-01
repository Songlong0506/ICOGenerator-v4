using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

public class CreateDocumentController : Controller
{
    private readonly AppDbContext _db;
    private readonly BARequirementService _baRequirementService;
    private readonly AgentRunService _agentRunService;
    private readonly AgentJobRunner _agentJobRunner;
    private readonly IConfiguration _configuration;

    public CreateDocumentController(
       AppDbContext db,
       BARequirementService baRequirementService,
       AgentRunService agentRunService,
       AgentJobRunner agentJobRunner,
       IConfiguration configuration)
    {
        _db = db;
        _baRequirementService = baRequirementService;
        _agentRunService = agentRunService;
        _agentJobRunner = agentJobRunner;
        _configuration = configuration;
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
        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstAsync(x => x.Id == projectId);

        var draftDocs = project.Documents
            .Where(x => x.VersionName == "draft" && !x.IsApproved)
            .ToList();

        if (!draftDocs.Any())
            return RedirectToAction(nameof(Index), new { projectId });

        var nextVersion = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x => int.TryParse(x.VersionName.Replace("V", ""), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var versionName = $"V{nextVersion}";

        foreach (var doc in draftDocs)
        {
            doc.VersionName = versionName;
            doc.Folder = $"docs/{versionName}";
            doc.IsApproved = true;

            if (!string.IsNullOrWhiteSpace(doc.FilePath))
            {
                var fileName = Path.GetFileName(doc.FilePath);

                var docsFolder = Path.GetDirectoryName(
                    Path.GetDirectoryName(doc.FilePath)
                );

                var newFilePath = Path.Combine(docsFolder, versionName, fileName);

                doc.FilePath = newFilePath;
            }
        }

        RenameDraftFolder(project.Name, versionName);

        await _db.SaveChangesAsync();

        var dev = await _db.Agents.FirstOrDefaultAsync(x => x.Name == "Developer");

        if (dev != null)
        {
            var aiDesignSpec = draftDocs
    .FirstOrDefault(x => x.FileName == "AIDesignSpec.docx");

            if (aiDesignSpec == null)
            {
                TempData["Error"] = "AI Design Spec chưa được tạo. Vui lòng chat với BA trước khi approve.";
                return RedirectToAction(nameof(Index), new { projectId });
            }


            await _agentRunService.RunAsync(
                projectId,
                dev.Id,
                $"""
User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

# AI Design Spec

{aiDesignSpec.Content}
""");
        }

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

        _agentJobRunner.RunBARequirementJob(job.Id);

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

    private void RenameDraftFolder(string projectName, string versionName)
    {
        var root = _configuration["AgentWorkspace:RootPath"]
            ?? throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var projectFolder = MakeSafeFolderName(projectName);

        var draftPath = Path.Combine(root, projectFolder, "docs", "draft");
        var versionPath = Path.Combine(root, projectFolder, "docs", versionName);

        if (!Directory.Exists(draftPath))
            return;

        if (Directory.Exists(versionPath))
            Directory.Delete(versionPath, true);

        Directory.Move(draftPath, versionPath);
    }

    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}
