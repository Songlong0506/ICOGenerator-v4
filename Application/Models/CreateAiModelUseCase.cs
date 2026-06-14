using ICOGenerator.Data;
using ICOGenerator.Domain;

namespace ICOGenerator.Application.Models;

public class CreateAiModelUseCase
{
    private readonly AppDbContext _db;
    public CreateAiModelUseCase(AppDbContext db) => _db = db;

    public async Task ExecuteAsync(AiModel model)
    {
        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();
    }
}
