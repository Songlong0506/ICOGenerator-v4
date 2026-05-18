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
    private readonly IConfiguration _configuration;

    public CreateDocumentController(
        AppDbContext db,
        BARequirementService baRequirementService,
        AgentRunService agentRunService,
        IConfiguration configuration)
    {
        _db = db;
        _baRequirementService = baRequirementService;
        _agentRunService = agentRunService;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations.OrderBy(c => c.CreatedAt))
                .ThenInclude(x => x.Agent)
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return RedirectToAction("Index", "Projects");

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
        }

        RenameDraftFolder(project.Name, versionName);

        await _db.SaveChangesAsync();

        var dev = await _db.Agents.FirstOrDefaultAsync(x => x.Name == "Developer");

        if (dev != null)
        {
            var requirement = string.Join("\n\n---\n\n", draftDocs.Select(x =>
                $"# {x.FileName}\n\n{x.Content}"));

            await _agentRunService.RunAsync(
                projectId,
                dev.Id,
                $"""
Requirement đã được user approve.

Version: {versionName}

Nhiệm vụ của bạn:
- Đọc requirement bên dưới
- Sinh code theo requirement
- Build/test nếu có thể
- Không sửa requirement

{requirement}
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