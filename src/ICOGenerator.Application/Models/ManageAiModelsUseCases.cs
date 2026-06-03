using ICOGenerator.Application.Abstractions;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class ListAiModelsQuery
{
    private readonly IAppDbContext _db;
    public ListAiModelsQuery(IAppDbContext db) => _db = db;

    public Task<List<AiModel>> ExecuteAsync() => _db.AiModels
        .OrderByDescending(x => x.IsDefault)
        .ThenBy(x => x.Name)
        .ToListAsync();
}

public class CreateAiModelUseCase
{
    private readonly IAppDbContext _db;
    public CreateAiModelUseCase(IAppDbContext db) => _db = db;

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

public class UpdateAiModelUseCase
{
    private readonly IAppDbContext _db;
    public UpdateAiModelUseCase(IAppDbContext db) => _db = db;

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

        if (input.IsDefault)
        {
            var defaults = await _db.AiModels.Where(x => x.Id != input.Id && x.IsDefault).ToListAsync();
            foreach (var item in defaults)
                item.IsDefault = false;
            model.IsDefault = true;
        }
        else
        {
            model.IsDefault = false;
        }

        await _db.SaveChangesAsync();
        return true;
    }
}

public class SetDefaultAiModelUseCase
{
    private readonly IAppDbContext _db;
    public SetDefaultAiModelUseCase(IAppDbContext db) => _db = db;

    public async Task ExecuteAsync(Guid id)
    {
        var models = await _db.AiModels.ToListAsync();
        foreach (var model in models)
            model.IsDefault = model.Id == id;
        await _db.SaveChangesAsync();
    }
}

public class DeleteAiModelUseCase
{
    private readonly IAppDbContext _db;
    public DeleteAiModelUseCase(IAppDbContext db) => _db = db;

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

public enum DeleteAiModelResult
{
    NotFound,
    InUse,
    Deleted
}
