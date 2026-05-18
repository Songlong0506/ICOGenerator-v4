using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ICOGenerator.Controllers;

public class CreateDocumentController : Controller
{
    private readonly AppDbContext _db;
    private readonly AgentRunService _agentRunService;
    private readonly IConfiguration _configuration;

    public CreateDocumentController(
        AppDbContext db,
        AgentRunService agentRunService,
        IConfiguration configuration)
    {
        _db = db;
        _agentRunService = agentRunService;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var project = await _db.Projects
            .Include(x => x.Documents.OrderByDescending(d => d.CreatedAt))
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
        if (string.IsNullOrWhiteSpace(message))
            return RedirectToAction(nameof(Index), new { projectId });

        var ba = await _db.Agents.FirstAsync(x => x.Name == "BA");

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = message,
            TokenUsed = EstimateTokens(message)
        });

        await _db.SaveChangesAsync();

        var result = await _agentRunService.RunAsync(projectId, ba.Id, message);

        var documentCount = await _db.ProjectDocuments
            .CountAsync(x => x.ProjectId == projectId && x.Folder == "01_Requirement");

        _db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Folder = "docs/draft",
            VersionName = "draft",
            IsApproved = false,
            FileName = $"Requirement_{DateTime.Now:yyyyMMdd_HHmmss}.md",
            Content = result,
            TokenUsed = EstimateTokens(result)
        });

        await _db.SaveChangesAsync();

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

        var nextVersionNumber = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x =>
            {
                var numberText = x.VersionName.Replace("V", "");
                return int.TryParse(numberText, out var n) ? n : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;

        var versionName = $"V{nextVersionNumber}";

        foreach (var doc in draftDocs)
        {
            doc.VersionName = versionName;
            doc.Folder = $"docs/{versionName}";
            doc.IsApproved = true;
        }

        var workspaceRoot = GetProjectWorkspacePath(project.Name);

        var draftPath = Path.Combine(workspaceRoot, "docs", "draft");
        var versionPath = Path.Combine(workspaceRoot, "docs", versionName);

        if (Directory.Exists(draftPath))
        {
            if (Directory.Exists(versionPath))
                Directory.Delete(versionPath, true);

            Directory.Move(draftPath, versionPath);
        }

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewChat(Guid projectId)
    {
        var project = await _db.Projects.FirstAsync(x => x.Id == projectId);

        var workspaceRoot = GetProjectWorkspacePath(project.Name);
        var draftPath = Path.Combine(workspaceRoot, "docs", "draft");

        if (Directory.Exists(draftPath))
            Directory.Delete(draftPath, true);

        Directory.CreateDirectory(draftPath);

        return RedirectToAction(nameof(Index), new { projectId });
    }

    private static int EstimateTokens(string text)
    {
        return Math.Max(1, text.Length / 4);
    }

    private string GetProjectWorkspacePath(string projectName)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var safeProjectName = MakeSafeFolderName(projectName);

        return Path.GetFullPath(Path.Combine(rootPath, safeProjectName));
    }

    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}