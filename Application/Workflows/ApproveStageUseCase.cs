using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Workflows;

/// <summary>
/// Releases a delivery workflow paused at a human-approval gate: flips the gated task
/// (<see cref="AgentTaskStatus.NeedsReview"/>) back to Queued so the background worker resumes the
/// pipeline. Picks the project's most recent run that is waiting for a human.
/// </summary>
public class ApproveStageUseCase
{
    private readonly AppDbContext _db;

    public ApproveStageUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ApproveStageResult> ExecuteAsync(Guid projectId)
    {
        var run = await _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (run == null)
            return ApproveStageResult.NoPendingApproval;

        var pending = await _db.AgentTasks
            .Where(x => x.WorkflowRunId == run.Id && x.Status == AgentTaskStatus.NeedsReview)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (pending == null)
            return ApproveStageResult.NoPendingApproval;

        pending.Status = AgentTaskStatus.Queued;
        run.Status = WorkflowRunStatus.Queued;
        await _db.SaveChangesAsync();

        return ApproveStageResult.Approved;
    }
}
