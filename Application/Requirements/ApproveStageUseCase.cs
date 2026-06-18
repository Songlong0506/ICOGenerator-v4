using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ApproveStageResult
{
    Advanced,       // đã enqueue bước kế
    Completed,      // không còn bước kế → workflow hoàn tất
    NoPendingStage, // không có workflow nào đang chờ duyệt
    MissingAgent    // không tìm thấy agent cho vai của bước kế
}

/// <summary>
/// Cổng duyệt: khi một bước delivery đã chạy xong và workflow đang ở
/// <see cref="WorkflowRunStatus.WaitingForHuman"/>, người dùng bấm duyệt để mở bước kế.
/// Đây là chỗ enqueue task cho bước tiếp theo (worker chỉ chạy, không tự nhảy bước).
/// </summary>
public class ApproveStageUseCase
{
    private readonly AppDbContext _db;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public ApproveStageUseCase(AppDbContext db, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
    }

    public async Task<ApproveStageResult> ExecuteAsync(Guid projectId, Guid? runId = null)
    {
        var run = await FindPendingRunAsync(projectId, runId);
        if (run == null)
            return ApproveStageResult.NoPendingStage;

        var next = DeliveryPipeline.Next(run.CurrentStage);
        if (next == null)
        {
            // Phòng thủ: không còn bước kế thì coi như hoàn tất.
            run.Status = WorkflowRunStatus.Completed;
            run.CurrentStage = WorkflowStageKey.Completed;
            run.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return ApproveStageResult.Completed;
        }

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.RoleKey == next.Role);
        if (agent == null)
            return ApproveStageResult.MissingAgent;

        var input = await ResolveInputAsync(projectId, run, next.InputSource);

        _db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = run.Id,
            ProjectId = projectId,
            AgentId = agent.Id,
            Type = next.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = next.Title,
            Input = input
        });

        run.CurrentStage = next.Stage;
        run.Status = WorkflowRunStatus.Queued; // worker sẽ chuyển sang Running khi nhận task

        await _db.SaveChangesAsync();
        return ApproveStageResult.Advanced;
    }

    private async Task<WorkflowRun?> FindPendingRunAsync(Guid projectId, Guid? runId)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        return await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
    }

    private async Task<string> ResolveInputAsync(Guid projectId, WorkflowRun run, PipelineInputSource source)
    {
        if (source == PipelineInputSource.DesignSpec)
        {
            var spec = await _db.ProjectDocuments
                .Where(d => d.ProjectId == projectId
                            && d.IsApproved
                            && d.FileName == _artifactCatalog.AiDesignSpec.FileName)
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefaultAsync();

            return spec?.Content ?? string.Empty;
        }

        // PreviousOutput: output của bước vừa hoàn tất (stage hiện tại của run).
        var lastOutput = await _db.AgentTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == AgentTaskStatus.Completed)
            .OrderByDescending(t => t.FinishedAt)
            .Select(t => t.Output)
            .FirstOrDefaultAsync();

        return lastOutput ?? string.Empty;
    }
}
