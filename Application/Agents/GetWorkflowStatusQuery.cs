using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record WorkflowTaskStatusVm(
    string Title, string Type, string Status, string? AgentName,
    int Attempt, string? Error, string? StartedAt, string? FinishedAt);

public record WorkflowProgressEventVm(long Seq, string At, string Kind, string Message, string? Detail)
{
    // Single mapper from the in-memory progress event to its wire VM, shared by every query that
    // surfaces progress (GetWorkflowStatusQuery, GetAgentActivityQuery, StreamWorkflowProgressQuery)
    // so the timestamp format lives in one place.
    public static WorkflowProgressEventVm From(WorkflowProgressEvent ev) =>
        new(ev.Seq, ev.At.ToString("o"), ev.Kind, ev.Message, ev.Detail);
}

/// <summary>
/// Một bước trên dải timeline của Delivery Pipeline. <paramref name="State"/> là trạng thái hiển thị
/// đã tính sẵn cho UI: "done" (đã xong), "running" (đang chạy), "next" (bước kế chờ duyệt),
/// "failed" (thất bại), "pending" (chưa tới). Tính ở server để timeline bám đúng nguồn sự thật
/// <see cref="DeliveryPipeline.Steps"/> thay vì lặp lại danh sách bước trong JS.
/// </summary>
public record PipelineStageVm(string Stage, string Title, string State);

public record WorkflowStatusVm(
    bool HasWorkflow, string? RunName, string? RunStatus, bool IsTerminal, bool IsCompleted,
    IReadOnlyList<WorkflowTaskStatusVm> Tasks, IReadOnlyList<WorkflowProgressEventVm> Events, long LastEventSeq,
    string RunKind,
    Guid? RunId, string? CurrentStage, bool IsWaitingForHuman, string? NextStageTitle, bool PocReady,
    bool NeedsMoreInfo, IReadOnlyList<PipelineStageVm> Pipeline);

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
                x.Status,
                x.CurrentStage
            })
            .FirstOrDefaultAsync();

        if (run == null)
            return new WorkflowStatusVm(false, null, null, true, false,
                Array.Empty<WorkflowTaskStatusVm>(), Array.Empty<WorkflowProgressEventVm>(), afterSeq, "Delivery",
                null, null, false, null, false, false, Array.Empty<PipelineStageVm>());

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
                x.StartedAt != null ? x.StartedAt.Value.ToString("o") : null,
                x.FinishedAt != null ? x.FinishedAt.Value.ToString("o") : null))
            .ToListAsync();

        var events = _progress.GetEvents(run.Id, afterSeq)
            .Select(WorkflowProgressEventVm.From)
            .ToList();

        var lastSeq = events.Count > 0 ? events[^1].Seq : afterSeq;

        var runKind = tasks.Any(t => t.Type == nameof(AgentTaskType.RequirementAnalysis))
            ? "Requirement"
            : "Delivery";

        var isWaiting = run.Status == WorkflowRunStatus.WaitingForHuman;
        var nextStep = DeliveryPipeline.Next(run.CurrentStage);
        var pocReady = tasks.Any(t => t.Type == nameof(AgentTaskType.PocPreview)
                                      && t.Status == nameof(AgentTaskStatus.Completed));

        // Cổng kiểm tra đã bỏ qua việc sinh tài liệu (thiếu thông tin) → UI hiển thị banner "cần bổ sung"
        // thay vì "đã tạo tài liệu". Output không nằm trong projection tasks (để rỗng), nên đọc riêng.
        var needsMoreInfo = await _db.AgentTasks
            .AsNoTracking()
            .AnyAsync(x => x.WorkflowRunId == run.Id
                          && x.Type == AgentTaskType.RequirementAnalysis
                          && x.Output == RequirementDraftMarkers.NeedsMoreInfo);

        // Pipeline timeline chỉ áp cho run delivery (POC → … → PR). Run "Requirement" (sinh tài liệu)
        // không chạy các bước này nên để rỗng — UI cũng chỉ render timeline cho run delivery.
        var pipeline = runKind == "Delivery"
            ? BuildPipeline(run.CurrentStage, run.Status, tasks)
            : Array.Empty<PipelineStageVm>();

        return new WorkflowStatusVm(
            true, run.Name, run.Status.ToString(),
            isTerminal, run.Status == WorkflowRunStatus.Completed,
            tasks, events, lastSeq, runKind,
            run.Id, run.CurrentStage.ToString(), isWaiting, nextStep?.Title, pocReady, needsMoreInfo, pipeline);
    }

    /// <summary>
    /// Quy đổi trạng thái run + các task đã chạy thành dải timeline theo đúng thứ tự
    /// <see cref="DeliveryPipeline.Steps"/>. Mỗi bước nhận một trạng thái hiển thị để JS chỉ việc vẽ.
    /// </summary>
    private static IReadOnlyList<PipelineStageVm> BuildPipeline(
        WorkflowStageKey currentStage, WorkflowRunStatus status,
        IReadOnlyList<WorkflowTaskStatusVm> tasks)
    {
        var completedTypes = tasks
            .Where(t => t.Status == nameof(AgentTaskStatus.Completed))
            .Select(t => t.Type)
            .ToHashSet();

        var runCompleted = status == WorkflowRunStatus.Completed;

        // Bước sửa lỗi (BugFix) là chu trình quanh Testing, không nằm trong chuỗi tuyến tính →
        // khi run đang ở BugFix, hiển thị Testing là bước đang chạy thay vì để cả dải "chờ".
        var effectiveCurrent = currentStage == WorkflowStageKey.BugFix
            ? WorkflowStageKey.Testing
            : currentStage;

        // Khi chờ duyệt, bước hiện tại đã xong và bước kế là điểm hành động tiếp theo của người dùng.
        var nextStage = status == WorkflowRunStatus.WaitingForHuman
            ? DeliveryPipeline.Next(currentStage)?.Stage
            : null;

        return DeliveryPipeline.Steps.Select(step =>
        {
            string state;
            if (runCompleted || completedTypes.Contains(step.TaskType.ToString()))
                state = "done";
            else if (step.Stage == effectiveCurrent)
                state = status switch
                {
                    WorkflowRunStatus.Failed => "failed",
                    WorkflowRunStatus.WaitingForHuman => "done",
                    _ => "running"
                };
            else if (step.Stage == nextStage)
                state = "next";
            else
                state = "pending";

            return new PipelineStageVm(step.Stage.ToString(), step.Title, state);
        }).ToList();
    }
}
