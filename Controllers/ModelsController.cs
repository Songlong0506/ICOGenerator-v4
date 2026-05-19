using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

public class ModelsController : Controller
{
    private readonly AppDbContext _db;

    public ModelsController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _db.AiModels
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AiModel model)
    {
        if (model.IsDefault)
        {
            var defaults = await _db.AiModels.Where(x => x.IsDefault).ToListAsync();
            foreach (var m in defaults)
                m.IsDefault = false;
        }

        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AiModel input)
    {
        var model = await _db.AiModels.FirstOrDefaultAsync(x => x.Id == input.Id);

        if (model == null)
            return NotFound();

        model.Name = input.Name;
        model.Provider = input.Provider;
        model.ModelId = input.ModelId;
        model.Endpoint = input.Endpoint;
        model.ApiKey = input.ApiKey;
        model.ContextWindow = input.ContextWindow;
        model.IsActive = input.IsActive;

        if (input.IsDefault)
        {
            var defaults = await _db.AiModels
                .Where(x => x.Id != input.Id && x.IsDefault)
                .ToListAsync();

            foreach (var item in defaults)
                item.IsDefault = false;

            model.IsDefault = true;
        }
        else
        {
            model.IsDefault = false;
        }

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(Guid id)
    {
        var models = await _db.AiModels.ToListAsync();

        foreach (var model in models)
            model.IsDefault = model.Id == id;

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var model = await _db.AiModels.FirstOrDefaultAsync(x => x.Id == id);

        if (model == null)
            return RedirectToAction(nameof(Index));

        var isUsed = await _db.Agents.AnyAsync(x => x.AiModelId == id);

        if (isUsed)
        {
            TempData["Error"] = "Model đang được Agent sử dụng, không thể xóa.";
            return RedirectToAction(nameof(Index));
        }

        _db.AiModels.Remove(model);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
}
