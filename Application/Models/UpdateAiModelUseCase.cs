using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class UpdateAiModelUseCase
{
    private readonly AppDbContext _db;
    public UpdateAiModelUseCase(AppDbContext db) => _db = db;

    public async Task<bool> ExecuteAsync(AiModel input)
    {
        var model = await _db.AiModels.FirstOrDefaultAsync(x => x.Id == input.Id);
        if (model == null)
            return false;

        model.Name = input.Name;
        model.Provider = input.Provider;
        model.ModelId = input.ModelId;
        model.Endpoint = input.Endpoint;
        model.ApiKey = input.ApiKey;
        model.ContextWindow = input.ContextWindow;
        model.IsActive = input.IsActive;

        await _db.SaveChangesAsync();
        return true;
    }
}
