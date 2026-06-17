using ICOGenerator.Data;
using ICOGenerator.Domain;

namespace ICOGenerator.Application.Models;

public class CreateAiModelUseCase
{
    private readonly AppDbContext _db;
    public CreateAiModelUseCase(AppDbContext db) => _db = db;

    public async Task ExecuteAsync(AiModel model)
    {
        // Identity & audit fields are server-owned; overwrite whatever the form posted so a
        // client can't over-post a chosen Id or a fake CreatedAt (mass assignment).
        model.Id = Guid.NewGuid();
        model.CreatedAt = DateTime.UtcNow;

        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();
    }
}
