using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// One agent that currently has work in flight (its task is Running or Retrying). Drives the
// "đang chạy" indicator on the Manage Agent dashboard.
public record ActiveAgentVm(
    Guid AgentId, string TaskTitle, string TaskType, string Status,
    Guid WorkflowRunId, string? StartedAt, int Attempt);

// The live operation feed for one agent's in-flight (or most recent) task. Powers the debug popup:
// the progress events are the actual steps the agent is taking (thinking / tool / observation / …).
public record AgentActivityVm(
    bool HasActivity, bool IsRunning,
    Guid AgentId, string? AgentName,
    string? TaskTitle, string? TaskType, string? TaskStatus,
    Guid? WorkflowRunId, string? StartedAt, string? FinishedAt, int Attempt, string? Error,
    IReadOnlyList<WorkflowProgressEventVm> Events, long LastEventSeq);

public class GetAgentActivityQuery
{
    private readonly AppDbContext _db;
    private readonly IWorkflowProgressReporter _progress;

    public GetAgentActivityQuery(AppDbContext db, IWorkflowProgressReporter progress)
    {
        _db = db;
        _progress = progress;
    }

    public async Task<IReadOnlyList<ActiveAgentVm>> GetActiveAgentsAsync(Guid projectId)
    {
        return await _db.AgentTasks
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId
                        && x.AgentId != null
                        && (x.Status == AgentTaskStatus.Running || x.Status == AgentTaskStatus.Retrying))
            .OrderByDescending(x => x.StartedAt)
            .Select(x => new ActiveAgentVm(
                x.AgentId!.Value,
                x.Title,
                x.Type.ToString(),
                x.Status.ToString(),
                x.WorkflowRunId,
                x.StartedAt != null ? x.StartedAt.Value.ToString("o") : null,
                x.Attempt))
            .ToListAsync();
    }

    public async Task<AgentActivityVm> GetAgentActivityAsync(Guid projectId, Guid agentId, long afterSeq = 0)
    {
        var roleKey = await _db.Agents.AsNoTracking()
            .Where(x => x.Id == agentId)
            .Select(x => (AgentRoleKey?)x.RoleKey)
            .FirstOrDefaultAsync();
        var agentName = roleKey?.GetTitle();

        // Prefer an in-flight task; otherwise fall back to the agent's most recent one so the popup
        // still shows what just happened if the work finished between a poll and the user's click.
        var task = await _db.AgentTasks
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId)
            .OrderByDescending(x => x.Status == AgentTaskStatus.Running || x.Status == AgentTaskStatus.Retrying)
            .ThenByDescending(x => x.StartedAt ?? x.CreatedAt)
            .Select(x => new
            {
                x.Title,
                x.Type,
                x.Status,
                x.WorkflowRunId,
                x.StartedAt,
                x.FinishedAt,
                x.Attempt,
                x.Error
            })
            .FirstOrDefaultAsync();

        if (task == null)
            return new AgentActivityVm(
                false, false, agentId, agentName,
                null, null, null, null, null, null, 0, null,
                Array.Empty<WorkflowProgressEventVm>(), afterSeq);

        var isRunning = task.Status is AgentTaskStatus.Running or AgentTaskStatus.Retrying;

        // Progress events are kept in-memory per workflow run; they are the agent's actual steps.
        var events = _progress.GetEvents(task.WorkflowRunId, afterSeq)
            .Select(WorkflowProgressEventVm.From)
            .ToList();

        var lastSeq = events.Count > 0 ? events[^1].Seq : afterSeq;

        return new AgentActivityVm(
            true, isRunning, agentId, agentName,
            task.Title, task.Type.ToString(), task.Status.ToString(),
            task.WorkflowRunId,
            task.StartedAt != null ? task.StartedAt.Value.ToString("o") : null,
            task.FinishedAt != null ? task.FinishedAt.Value.ToString("o") : null,
            task.Attempt, task.Error,
            events, lastSeq);
    }
}
