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
    private readonly IConfiguration _configuration;

    public ProjectsController(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var projects = await _db.Projects
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

        ViewBag.MockupMap = projects.ToDictionary(
            x => x.Id,
            x => System.IO.File.Exists(GetMockupPath(x.Name))
        );

        return View(projects);
    }

    [HttpPost]
    public async Task<IActionResult> Create(ProjectCreateVm vm)
    {
        if (!ModelState.IsValid)
            return RedirectToAction(nameof(Index));

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

    public async Task<IActionResult> Mockup(Guid projectId)
    {
        var project = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return NotFound();

        var filePath = GetMockupPath(project.Name);

        if (!System.IO.File.Exists(filePath))
            return NotFound("Mockup file not found.");

        return PhysicalFile(filePath, "text/html", enableRangeProcessing: true);
    }

    private string GetMockupPath(string projectName)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var safeProjectName = MakeSafeFolderName(projectName);

        return Path.Combine(rootPath, safeProjectName, "poc-demo.html");
    }

    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}
