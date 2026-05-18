using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ICOGenerator.Controllers;
public class CreateDocumentController : Controller
{
    private readonly AppDbContext _db;
    private readonly AgentRunService _agentRunService;
    public CreateDocumentController(AppDbContext db, AgentRunService agentRunService) { _db = db; _agentRunService = agentRunService; }
    public async Task<IActionResult> Index(Guid projectId)
    {
        var project = await _db.Projects.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null) return RedirectToAction("Index", "Projects");
        return View(project);
    }
    [HttpPost]
    public async Task<IActionResult> Chat(Guid projectId, string message)
    {
        var ba = await _db.Agents.FirstAsync(x => x.Name == "BA");
        var result = await _agentRunService.RunAsync(projectId, ba.Id, message);
        TempData["AgentResult"] = result;
        return RedirectToAction(nameof(Index), new { projectId });
    }
}
