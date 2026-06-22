using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Persists MAF workflow checkpoints to the database (<see cref="WorkflowCheckpoint"/>), making a
/// delivery-pipeline run durable: it survives app restarts and can be resumed after a human-approval
/// gate. This is the EF equivalent of the framework's built-in file-system JSON store.
///
/// A fresh DbContext is resolved per operation (via the scope factory) rather than holding one for the
/// whole run, because a run can sit idle for a long time at an approval gate — a long-lived DbContext
/// would be a leak/threading hazard.
/// </summary>
public sealed class EfWorkflowCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EfWorkflowCheckpointStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var checkpointId = Guid.NewGuid().ToString("n");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.WorkflowCheckpoints.Add(new WorkflowCheckpoint
        {
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ParentCheckpointId = parent?.CheckpointId,
            Data = value.GetRawText()
        });
        await db.SaveChangesAsync();

        return new CheckpointInfo(sessionId, checkpointId);
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkflowCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionId == sessionId && x.CheckpointId == key.CheckpointId)
            ?? throw new InvalidOperationException($"Checkpoint {key.CheckpointId} not found for session {sessionId}.");

        // Clone so the value outlives the JsonDocument we parsed it from.
        using var doc = JsonDocument.Parse(row.Data);
        return doc.RootElement.Clone();
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.WorkflowCheckpoints.AsNoTracking().Where(x => x.SessionId == sessionId);
        if (withParent is not null)
            query = query.Where(x => x.ParentCheckpointId == withParent.CheckpointId);

        var ids = await query
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.CheckpointId)
            .ToListAsync();

        return ids.Select(id => new CheckpointInfo(sessionId, id)).ToList();
    }
}
