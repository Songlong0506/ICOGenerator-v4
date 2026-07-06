using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum RequestStageRevisionResult
{
    /// <summary>Đã enqueue task chỉnh sửa cho bước hiện tại — worker sẽ chạy lại với nhận xét.</summary>
    Queued,

    /// <summary>Không có workflow nào đang chờ duyệt để yêu cầu chỉnh sửa.</summary>
    NoWaitingRun,

    /// <summary>Nhận xét trống — không có gì để agent sửa theo.</summary>
    MissingFeedback,

    /// <summary>Đã dùng hết số vòng chỉnh sửa cho bước này (xem <see cref="DeliveryPipeline.MaxRevisionRounds"/>).</summary>
    RevisionLimitReached
}

/// <summary>
/// Cổng duyệt — lựa chọn thứ ba bên cạnh Duyệt/Từ chối: người duyệt thấy kết quả bước GẦN đúng,
/// gửi nhận xét để agent CHỈNH SỬA lại đúng bước đó thay vì hủy cả workflow (Reject) rồi chạy lại
/// từ đầu, vốn đốt lại token của mọi bước đã xong. Task chỉnh sửa giữ nguyên Input gốc của bước
/// (spec / output bước trước) và mang nhận xét ở <see cref="AgentTask.RevisionFeedback"/>; worker
/// chạy xong lại rơi về cổng duyệt của CHÍNH bước này (AdvanceLinearPipeline giữ CurrentStage).
///
/// Khác với Reject, chỉnh sửa được phép cả ở cổng POC: nhận xét ở đây là "POC chưa bám đúng spec
/// đã duyệt / cần chỉnh UI", KHÔNG phải đổi requirement (việc đó vẫn là của user qua chat BA).
///
/// Mỗi bước có trần <see cref="DeliveryPipeline.MaxRevisionRounds"/> vòng chỉnh sửa để kết quả
/// không hội tụ thì dừng lại thay vì đốt token vô hạn.
/// </summary>
public class RequestStageRevisionUseCase
{
    private readonly AppDbContext _db;

    public RequestStageRevisionUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RequestStageRevisionResult> ExecuteAsync(Guid projectId, string? feedback, Guid? runId = null)
    {
        feedback = feedback?.Trim();
        if (string.IsNullOrEmpty(feedback))
            return RequestStageRevisionResult.MissingFeedback;

        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return RequestStageRevisionResult.NoWaitingRun;

        // Khi chờ duyệt, CurrentStage là bước VỪA chạy xong — đó là bước cần chỉnh sửa.
        var step = DeliveryPipeline.Find(run.CurrentStage);
        if (step == null)
            return RequestStageRevisionResult.NoWaitingRun;

        // Task gần nhất đã hoàn tất của bước này (bản gốc hoặc bản chỉnh sửa trước đó) — nguồn
        // Input nguyên bản và agent đảm nhiệm. Không có thì run đang ở trạng thái bất thường.
        var previousTask = await _db.AgentTasks
            .Where(t => t.WorkflowRunId == run.Id
                        && t.Type == step.TaskType
                        && t.Status == AgentTaskStatus.Completed)
            .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (previousTask == null)
            return RequestStageRevisionResult.NoWaitingRun;

        var revisionsUsed = await _db.AgentTasks.CountAsync(
            t => t.WorkflowRunId == run.Id
                 && t.Type == step.TaskType
                 && t.RevisionFeedback != null);

        if (revisionsUsed >= DeliveryPipeline.MaxRevisionRounds)
            return RequestStageRevisionResult.RevisionLimitReached;

        _db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = run.Id,
            ProjectId = projectId,
            AgentId = previousTask.AgentId,
            Type = step.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = $"{step.Title} (chỉnh sửa lần {revisionsUsed + 1})",
            Input = previousTask.Input,
            RevisionFeedback = feedback
        });

        // CurrentStage giữ nguyên — chạy xong bước này lại rơi về đúng cổng duyệt hiện tại.
        run.Status = WorkflowRunStatus.Queued;

        await _db.SaveChangesAsync();
        return RequestStageRevisionResult.Queued;
    }
}
