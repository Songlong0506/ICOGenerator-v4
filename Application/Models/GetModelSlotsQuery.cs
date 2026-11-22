using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

public class GetModelSlotsQuery
{
    private readonly AppDbContext _db;
    public GetModelSlotsQuery(AppDbContext db) => _db = db;

    public async Task<ModelSlotsVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var assignments = await _db.ModelSlotAssignments
            .AsNoTracking()
            .Select(x => new { x.Slot, x.AiModelId, x.AiModel.IsActive, x.UpdatedByUsername, x.UpdatedAt })
            .ToListAsync(cancellationToken);

        var assignedIds = assignments.Select(x => x.AiModelId).ToList();
        var models = await _db.AiModels
            .AsNoTracking()
            .Where(x => x.IsActive || assignedIds.Contains(x.Id))
            .OrderBy(x => x.ModelId)
            .ToListAsync(cancellationToken);

        var slots = Enum.GetValues<ModelSlot>()
            .Select(slot => assignments.FirstOrDefault(a => a.Slot == slot) is { } a
                ? new ModelSlotRow(slot, a.AiModelId, !a.IsActive, a.UpdatedByUsername, a.UpdatedAt)
                : new ModelSlotRow(slot, null, false, null, null))
            .ToList();

        return new ModelSlotsVm(slots, models);
    }
}
