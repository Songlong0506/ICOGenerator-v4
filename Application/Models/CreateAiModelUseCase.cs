using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;

namespace ICOGenerator.Application.Models;

public class CreateAiModelUseCase
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public CreateAiModelUseCase(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task ExecuteAsync(AiModel model)
    {
        // Identity & audit fields are server-owned; overwrite whatever the form posted so a client
        // can't over-post a chosen Id or fake CreatedAt (mass assignment).
        model.Id = Guid.NewGuid();
        model.CreatedAt = DateTime.UtcNow;

        _db.AiModels.Add(model);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditCategory.Model, AuditAction.Create, model.Id.ToString(),
            $"Tạo AI Model \"{model.Name}\"", after: Snapshot(model));
    }

    // Ảnh chụp các trường có ý nghĩa để debug; KHÔNG kèm ApiKey (AuditLogger cũng tự che theo tên trường).
    internal static object Snapshot(AiModel m) => new
    {
        m.Name,
        m.Provider,
        m.ModelId,
        m.Endpoint,
        m.ContextWindow,
        m.InputPricePerMillionTokens,
        m.OutputPricePerMillionTokens,
        m.IsActive,
        m.SupportsVision
    };
}
