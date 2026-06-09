using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record WorkflowTaskStatusVm(
    string Title, string Type, string Status, string? AgentName,
    int Attempt, string? Error, string? Output, string? StartedAt, string? FinishedAt);

public record WorkflowStatusVm(
    bool HasWorkflow, string? RunName, string? RunStatus, string? Stage,
    string? StartedAt, string? FinishedAt, bool IsTerminal, bool IsCompleted,
    IReadOnlyList<WorkflowTaskStatusVm> Tasks);

public class GetWorkflowStatusQuery
{
    private readonly AppDbContext _db;
    public GetWorkflowStatusQuery(AppDbContext db) => _db = db;

    public async Task<WorkflowStatusVm> ExecuteAsync(Guid projectId)
    {
        var run = await _db.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Name,
                x.Status,
                x.CurrentStage,
                x.StartedAt,
                x.FinishedAt,
                Tasks = x.AgentTasks.OrderBy(t => t.CreatedAt).Select(t => new
                {
                    t.Title,
                    t.Type,
                    t.Status,
                    AgentName = t.Agent != null ? t.Agent.Name : null,
                    t.Attempt,
                    t.Error,
                    t.Output,
                    t.StartedAt,
                    t.FinishedAt
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (run == null)
            return new WorkflowStatusVm(false, null, null, null, null, null, true, false, Array.Empty<WorkflowTaskStatusVm>());

        static string? Fmt(DateTime? d) => d?.ToLocalTime().ToString("HH:mm:ss");
        static string? Preview(string? s) =>
            string.IsNullOrEmpty(s) ? null : (s.Length > 600 ? s[..600] + "…" : s);

        var isTerminal = run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;

        var tasks = run.Tasks.Select(t => new WorkflowTaskStatusVm(
            t.Title, t.Type.ToString(), t.Status.ToString(), t.AgentName,
            t.Attempt, t.Error, Preview(t.Output), Fmt(t.StartedAt), Fmt(t.FinishedAt))).ToList();

        return new WorkflowStatusVm(
            true, run.Name, run.Status.ToString(), run.CurrentStage.ToString(),
            Fmt(run.StartedAt), Fmt(run.FinishedAt),
            isTerminal, run.Status == WorkflowRunStatus.Completed, tasks);
    }
}
