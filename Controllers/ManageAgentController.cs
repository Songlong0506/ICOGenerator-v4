using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.ViewModels;
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
             .AsNoTracking()
             .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return RedirectToAction("Index", "Projects");

        project.Documents = await _db.ProjectDocuments
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Folder)
            .ThenBy(x => x.FileName)
            .ToListAsync();

        project.Conversations = await _db.AgentConversations
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Include(x => x.Agent)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync();

        project.ModelCallLogs = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
    .OrderByDescending(x => x.CreatedAt)
    .Take(100)
    .Select(x => new AgentModelCallLog
    {
        Id = x.Id,
        ModelName = x.ModelName,
        TotalTokens = x.TotalTokens,
        DurationMs = x.DurationMs,
        //Status = x.Status,
        CreatedAt = x.CreatedAt
    })
    .ToListAsync();

        ViewBag.Agents = await _db.Agents
            .AsNoTracking()
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .ToListAsync();

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
