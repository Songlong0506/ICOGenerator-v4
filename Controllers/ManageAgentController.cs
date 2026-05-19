using ICOGenerator.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

public class ManageAgentController : Controller
{
    private readonly AppDbContext _db;

    public ManageAgentController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .ThenInclude(x => x.Agent)
            .Include(x => x.ModelCallLogs)
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null) return RedirectToAction("Index", "Projects");

        ViewBag.Agents = await _db.Agents.Include(x => x.AgentTools).ToListAsync();
        return View(project);
    }

    [HttpGet]
    public async Task<IActionResult> AgentCallLogs(Guid projectId, Guid agentId)
    {
        var logs = await _db.AgentModelCallLogs
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.AgentName,
                x.ModelName,
                x.ModelId,
                x.Endpoint,
                x.Purpose,
                x.Step,
                x.PromptTokens,
                x.CompletionTokens,
                x.TotalTokens,
                x.DurationMs,
                x.HttpStatusCode,
                x.IsSuccess,
                x.ErrorMessage,
                x.CreatedAt
            })
            .ToListAsync();

        return Json(logs);
    }

    [HttpGet]
    public async Task<IActionResult> CallLogDetail(Guid id)
    {
        var log = await _db.AgentModelCallLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (log == null) return NotFound();

        return Json(new
        {
            log.Id,
            log.ProjectId,
            log.AgentId,
            log.AgentName,
            log.ModelName,
            log.ModelId,
            log.Endpoint,
            log.Purpose,
            log.Step,
            log.RequestJson,
            log.ResponseText,
            log.ExtractedContent,
            log.ErrorMessage,
            log.PromptTokens,
            log.CompletionTokens,
            log.TotalTokens,
            log.DurationMs,
            log.HttpStatusCode,
            log.IsSuccess,
            log.CreatedAt
        });
    }
}
