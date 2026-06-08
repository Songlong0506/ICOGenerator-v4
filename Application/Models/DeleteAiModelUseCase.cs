using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class DeleteAiModelUseCase
{
    private readonly AppDbContext _db;
    public DeleteAiModelUseCase(AppDbContext db) => _db = db;

    public async Task<DeleteAiModelResult> ExecuteAsync(Guid id)
    {
        var model = await _db.AiModels.FirstOrDefaultAsync(x => x.Id == id);
        if (model == null)
            return DeleteAiModelResult.NotFound;

        var isUsed = await _db.Agents.AnyAsync(x => x.AiModelId == id);
        if (isUsed)
            return DeleteAiModelResult.InUse;

        _db.AiModels.Remove(model);
        await _db.SaveChangesAsync();
        return DeleteAiModelResult.Deleted;
    }
}
