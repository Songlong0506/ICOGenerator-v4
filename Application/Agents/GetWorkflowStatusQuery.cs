using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record WorkflowTaskStatusVm(
    string Title, string Type, string Status, string? AgentName,
    int Attempt, string? Error, string? Output, string? StartedAt, string? FinishedAt);

public record WorkflowStatusVm(bool HasWorkflow, string? RunName, string? RunStatus, bool IsTerminal, bool IsCompleted);

public class GetWorkflowStatusQuery
{
    private readonly AppDbContext _db;
    public GetWorkflowStatusQuery(AppDbContext db) => _db = db;

    public async Task<WorkflowStatusVm> ExecuteAsync(Guid projectId, Guid? runId = null)
    {
        var query = _db.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Name,
                x.Status
            })
            .FirstOrDefaultAsync();

        if (run == null)
            return new WorkflowStatusVm(false, null, null, true, false);

        var isTerminal = run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;

        return new WorkflowStatusVm(
            true, run.Name, run.Status.ToString(), 
            isTerminal, run.Status == WorkflowRunStatus.Completed);
    }
}
