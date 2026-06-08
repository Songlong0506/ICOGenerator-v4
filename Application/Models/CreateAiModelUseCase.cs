using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class CreateAiModelUseCase
{
    private readonly AppDbContext _db;
    public CreateAiModelUseCase(AppDbContext db) => _db = db;

    public async Task ExecuteAsync(AiModel model)
    {
        if (model.IsDefault)
        {
            var defaults = await _db.AiModels.Where(x => x.IsDefault).ToListAsync();
            foreach (var item in defaults)
                item.IsDefault = false;
        }

        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();
    }
}
