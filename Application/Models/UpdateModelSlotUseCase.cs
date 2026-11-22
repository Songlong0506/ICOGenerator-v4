using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

/// <summary>
/// Gán (hoặc bỏ gán) model cho một <see cref="ModelSlot"/>. <paramref name="aiModelId"/> null = bỏ gán ⇒ xoá
/// dòng, lời gọi xin suất này chạy model của agent gọi nó.
/// </summary>
public class UpdateModelSlotUseCase
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public UpdateModelSlotUseCase(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<UpdateModelSlotResult> ExecuteAsync(ModelSlot slot, Guid? aiModelId, string? username)
    {
        if (!Enum.IsDefined(slot))
            return UpdateModelSlotResult.UnknownSlot;

        // Model không tồn tại / đang tắt thì từ chối thay vì lặng lẽ lưu: người chọn tưởng đang thử model mới mà
        // thực ra resolver vẫn trả model của agent gọi.
        if (aiModelId is { } modelId && !await _db.AiModels.AnyAsync(x => x.Id == modelId && x.IsActive))
            return UpdateModelSlotResult.ModelUnavailable;

        var row = await _db.ModelSlotAssignments.FirstOrDefaultAsync(x => x.Slot == slot);
        var before = Snapshot(slot, row?.AiModelId);

        if (aiModelId is not { } newId)
        {
            if (row != null)
                _db.ModelSlotAssignments.Remove(row);
        }
        else if (row == null)
        {
            _db.ModelSlotAssignments.Add(new ModelSlotAssignment
            {
                Slot = slot,
                AiModelId = newId,
                UpdatedByUsername = username,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.AiModelId = newId;
            row.UpdatedByUsername = username;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditCategory.Model, AuditAction.Update, slot.ToString(),
            Loc.T("UpdateModelSlotUseCase.ExecuteAsync", slot.GetTitle()),
            before: before, after: Snapshot(slot, aiModelId));
        return UpdateModelSlotResult.Saved;
    }

    private static object Snapshot(ModelSlot slot, Guid? aiModelId) => new
    {
        Slot = slot.ToString(),
        AiModelId = aiModelId?.ToString()
    };
}
