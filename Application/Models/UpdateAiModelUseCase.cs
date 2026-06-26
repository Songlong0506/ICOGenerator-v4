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
        // The edit form no longer round-trips the decrypted key to the browser, so a blank field
        // means "keep the current key"; only overwrite when a new value was entered.
        if (!string.IsNullOrWhiteSpace(input.ApiKey))
            model.ApiKey = input.ApiKey;
        model.ContextWindow = input.ContextWindow;
        model.InputPricePerMillionTokens = input.InputPricePerMillionTokens;
        model.OutputPricePerMillionTokens = input.OutputPricePerMillionTokens;
        model.IsActive = input.IsActive;
        model.SupportsVision = input.SupportsVision;

        await _db.SaveChangesAsync();
        return true;
    }
}
