using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;
public class ModelsController : Controller
{
    private readonly AppDbContext _db;
    public ModelsController(AppDbContext db) { _db = db; }
    public async Task<IActionResult> Index() => View(await _db.AiModels.OrderByDescending(x => x.IsDefault).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create(AiModel model)
    {
        if (model.IsDefault)
        {
            var defaults = await _db.AiModels.Where(x => x.IsDefault).ToListAsync();
            foreach (var m in defaults) m.IsDefault = false;
        }
        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
