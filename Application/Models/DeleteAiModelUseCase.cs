using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class DeleteAiModelUseCase
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public DeleteAiModelUseCase(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<DeleteAiModelResult> ExecuteAsync(Guid id)
    {
        var model = await _db.AiModels.FirstOrDefaultAsync(x => x.Id == id);
        if (model == null)
            return DeleteAiModelResult.NotFound;

        var isUsed = await _db.Agents.AnyAsync(x => x.AiModelId == id);
        if (isUsed)
            return DeleteAiModelResult.InUse;

        var before = CreateAiModelUseCase.Snapshot(model);
        var modelId = model.ModelId;

        _db.AiModels.Remove(model);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditCategory.Model, AuditAction.Delete, id.ToString(),
            $"Xóa AI Model \"{modelId}\"", before: before);
        return DeleteAiModelResult.Deleted;
    }
}
