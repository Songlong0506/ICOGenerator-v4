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

    public CreateDocumentController(
        AppDbContext db,
        AgentRunService agentRunService)
    {
        _db = db;
        _agentRunService = agentRunService;
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
            Folder = "01_Requirement",
            FileName = $"Requirement_V{documentCount + 1}.md",
            Content = result,
            TokenUsed = EstimateTokens(result)
        });

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { projectId });
    }

    private static int EstimateTokens(string text)
    {
        return Math.Max(1, text.Length / 4);
    }
}