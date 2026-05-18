using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

public class ProjectsController : Controller
{
    private readonly AppDbContext _db;
    public ProjectsController(AppDbContext db) { _db = db; }

    public async Task<IActionResult> Index()
    {
        var projects = await _db.Projects.OrderByDescending(x => x.CreatedAt).ToListAsync();
        return View(projects);
    }

    [HttpPost]
    public async Task<IActionResult> Create(ProjectCreateVm vm)
    {
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        _db.Projects.Add(new Project
        {
            Name = vm.Name,
            Description = vm.Description,
            GenerationMode = vm.GenerationMode,
            BackendGitUrl = vm.BackendGitUrl,
            FrontendGitUrl = vm.FrontendGitUrl,
            Status = ProjectStatus.Planning
        });
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
