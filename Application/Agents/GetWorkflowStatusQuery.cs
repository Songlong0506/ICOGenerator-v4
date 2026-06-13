using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record WorkflowTaskStatusVm(
    string Title, string Type, string Status, string? AgentName,
    int Attempt, string? Error, string? Output, string? StartedAt, string? FinishedAt);

public record WorkflowProgressEventVm(long Seq, string At, string Kind, string Message, string? Detail);

public record WorkflowStatusVm(
    bool HasWorkflow, string? RunName, string? RunStatus, bool IsTerminal, bool IsCompleted,
    IReadOnlyList<WorkflowTaskStatusVm> Tasks, IReadOnlyList<WorkflowProgressEventVm> Events, long LastEventSeq,
    string RunKind);

public class GetWorkflowStatusQuery
{
    private readonly AppDbContext _db;
    private readonly IWorkflowProgressReporter _progress;

    public GetWorkflowStatusQuery(AppDbContext db, IWorkflowProgressReporter progress)
    {
        _db = db;
        _progress = progress;
    }

    public async Task<WorkflowStatusVm> ExecuteAsync(Guid projectId, Guid? runId = null, long afterSeq = 0)
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
                x.Id,
                x.Name,
                x.Status
            })
            .FirstOrDefaultAsync();

        if (run == null)
            return new WorkflowStatusVm(false, null, null, true, false,
                Array.Empty<WorkflowTaskStatusVm>(), Array.Empty<WorkflowProgressEventVm>(), afterSeq, "Delivery");

        var isTerminal = run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;

        var tasks = await _db.AgentTasks
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == run.Id)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new WorkflowTaskStatusVm(
                x.Title,
                x.Type.ToString(),
                x.Status.ToString(),
                x.Agent != null ? x.Agent.Name : null,
                x.Attempt,
                x.Error,
                null,
                x.StartedAt != null ? x.StartedAt.Value.ToString("o") : null,
                x.FinishedAt != null ? x.FinishedAt.Value.ToString("o") : null))
            .ToListAsync();

        var events = _progress.GetEvents(run.Id, afterSeq)
            .Select(x => new WorkflowProgressEventVm(x.Seq, x.At.ToString("o"), x.Kind, x.Message, x.Detail))
            .ToList();

        var lastSeq = events.Count > 0 ? events[^1].Seq : afterSeq;

        var runKind = tasks.Any(t => t.Type == nameof(AgentTaskType.RequirementAnalysis))
            ? "Requirement"
            : "Delivery";

        return new WorkflowStatusVm(
            true, run.Name, run.Status.ToString(),
            isTerminal, run.Status == WorkflowRunStatus.Completed,
            tasks, events, lastSeq, runKind);
    }
}
