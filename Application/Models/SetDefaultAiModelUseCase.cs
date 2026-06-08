using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class SetDefaultAiModelUseCase
{
    private readonly AppDbContext _db;
    public SetDefaultAiModelUseCase(AppDbContext db) => _db = db;

    public async Task ExecuteAsync(Guid id)
    {
        var models = await _db.AiModels.ToListAsync();
        foreach (var model in models)
            model.IsDefault = model.Id == id;
        await _db.SaveChangesAsync();
    }
}
