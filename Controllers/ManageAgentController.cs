using ICOGenerator.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ICOGenerator.Controllers;
public class ManageAgentController : Controller
{
    private readonly AppDbContext _db;
    public ManageAgentController(AppDbContext db) { _db = db; }
    public async Task<IActionResult> Index(Guid projectId)
    {
        var project = await _db.Projects.Include(x => x.Documents).Include(x => x.Conversations).ThenInclude(x => x.Agent).FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null) return RedirectToAction("Index", "Projects");
        ViewBag.Agents = await _db.Agents.Include(x => x.AgentTools).ToListAsync();
        return View(project);
    }
}
