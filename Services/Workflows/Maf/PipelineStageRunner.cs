using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Runs a single delivery-pipeline stage for the MAF workflow engine: resolves the agent for the
/// stage's role, records an <see cref="AgentTask"/> row (Running → Completed/Failed) so the existing
/// progress/status UI keeps working unchanged, performs the same per-stage setup the legacy worker did
/// (POC assets, Bosch skeleton, POC stop-when, max-step salvage handling), runs the agent, advances the
/// run's current stage, and returns the stage output.
/// </summary>
public interface IPipelineStageRunner
{
    Task<string> RunStageAsync(Guid workflowRunId, Guid projectId, PipelineStep step, string input, CancellationToken cancellationToken);
}

public sealed class PipelineStageRunner : IPipelineStageRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowProgressReporter _progress;

    public PipelineStageRunner(IServiceScopeFactory scopeFactory, IWorkflowProgressReporter progress)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
    }

    public async Task<string> RunStageAsync(Guid workflowRunId, Guid projectId, PipelineStep step, string input, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var agentId = await db.Agents
            .Where(a => a.RoleKey == step.Role)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Không tìm thấy agent vai {step.Role} cho bước \"{step.Title}\".");

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Không tìm thấy project {projectId} cho bước này.");

        // One AgentTask row per stage run, mirroring the legacy worker so status/activity/usage queries are unchanged.
        var task = new AgentTask
        {
            WorkflowRunId = workflowRunId,
            ProjectId = projectId,
            AgentId = agentId,
            Type = step.TaskType,
            Status = AgentTaskStatus.Running,
            Title = step.Title,
            Input = input,
            StartedAt = DateTime.UtcNow,
            Attempt = 1
        };
        db.AgentTasks.Add(task);

        var run = await db.WorkflowRuns.FirstAsync(r => r.Id == workflowRunId, cancellationToken);
        run.CurrentStage = step.Stage;
        run.Status = WorkflowRunStatus.Running;
        run.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _progress.Report(workflowRunId, "start", $"Bắt đầu task: {step.Title}");

        try
        {
            if (step.TaskType == AgentTaskType.PocPreview)
            {
                _progress.Report(workflowRunId, "setup", "Chuẩn bị workspace và template POC…");
                await sp.GetRequiredService<PocWorkspaceSeeder>().EnsureDesignAssetsAsync(project);
            }

            if (step.TaskType == AgentTaskType.Implementation && project.IsUseBoschTemplate)
            {
                _progress.Report(workflowRunId, "setup", "Bosch template: chuẩn bị skeleton (.NET + Angular)…");
                var skeletonKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
                var seedSummary = await sp.GetRequiredService<BoschTemplateSeeder>().SeedAsync(skeletonKey, cancellationToken);
                _progress.Report(workflowRunId, "setup", $"Bosch skeleton: {seedSummary}");
            }

            var prompt = sp.GetRequiredService<WorkflowTaskPromptBuilder>().Build(step.TaskType, input, project.IsUseBoschTemplate);

            // POC must call SetPocContent exactly once; stop as soon as it succeeds (same as the legacy worker).
            Func<string, string, bool>? stopWhen = step.TaskType == AgentTaskType.PocPreview
                ? (toolName, observation) =>
                    toolName.Equals(nameof(WorkspaceTools.SetPocContent), StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("POC content updated", StringComparison.OrdinalIgnoreCase)
                : null;

            var output = await sp.GetRequiredService<AgentRunService>().RunAsync(
                projectId,
                agentId,
                prompt,
                step.MaxSteps,
                onProgress: (kind, message, detail) => _progress.Report(workflowRunId, kind, message, detail),
                stopWhen: stopWhen,
                onToken: token => _progress.ReportToken(workflowRunId, token),
                workflowRunId: workflowRunId,
                cancellationToken: cancellationToken);

            // Max steps reached without a deliverable → fail the stage (same contract as the legacy worker).
            if (string.Equals(output, AgentRunService.MaxStepsReachedResult, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    step.TaskType == AgentTaskType.PocPreview
                        ? "Agent đạt giới hạn số bước mà chưa tạo được POC (chưa gọi SetPocContent thành công)."
                        : $"Agent đạt giới hạn số bước mà chưa hoàn tất bước '{step.Title}' (chưa gọi tool ghi kết quả).");

            task.Status = AgentTaskStatus.Completed;
            task.Output = output;
            task.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutting down mid-stage: leave the task Running so the resume reaper can pick the run back up.
            throw;
        }
        catch (Exception ex)
        {
            _progress.Report(workflowRunId, "error", "Task thất bại.", ex.Message);
            task.Status = AgentTaskStatus.Failed;
            task.Error = ex.Message;
            task.FinishedAt = DateTime.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* run will be marked failed by the driver */ }
            throw;
        }
    }
}
