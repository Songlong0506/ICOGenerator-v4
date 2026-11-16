using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum RetryWorkflowResult
{
    Requeued,       // đã đẩy lại bước thất bại vào hàng đợi, worker sẽ chạy lại
    NoFailedRun,    // không có workflow nào đang ở trạng thái Failed để thử lại
    NoRetryableTask // workflow Failed nhưng không tìm thấy task thất bại để chạy lại
}

/// <summary>
/// Một bước delivery (vd POC) có thể thất bại vì lỗi tạm thời — điển hình là LLM rớt kết nối
/// ("An existing connection was forcibly closed by the remote host"). Trước đây run rơi vào
/// <see cref="WorkflowRunStatus.Failed"/> (terminal) và người dùng buộc phải Approve lại từ đầu,
/// tốn token sinh lại toàn bộ tài liệu chỉ để chạy lại đúng bước đã hỏng.
///
/// Use case này đẩy LẠI đúng task đã thất bại vào hàng đợi (giữ nguyên Input), khôi phục stage thật
/// của bước đó rồi để <c>AgentTaskWorker</c> nhặt chạy lại. Không tạo workflow mới, không sinh lại
/// tài liệu — chỉ tiếp tục từ chỗ hỏng.
///
/// <para>
/// "Chỗ hỏng" tính tới TỪNG BƯỚC của agent: lượt agent của task ghi checkpoint bền sau mỗi bước
/// (<see cref="Domain.AgentRunCheckpoint"/>), nên mặc định worker chạy TIẾP đúng bước bị hỏng chứ không
/// dựng lại cả task. <c>restart: true</c> bỏ checkpoint đó để chạy lại từ đầu — lối thoát cho ca chính
/// hội thoại đã ghi là thứ làm lời gọi hỏng (vd quá dài so với context window của model).
/// </para>
/// </summary>
public class RetryWorkflowUseCase
{
    private readonly AppDbContext _db;

    public RetryWorkflowUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RetryWorkflowResult> ExecuteAsync(Guid projectId, Guid? runId = null, bool restart = false)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.Failed);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return RetryWorkflowResult.NoFailedRun;

        // Task thực sự đã hỏng (task Failed mới nhất của run). Đây là bước cần chạy lại.
        var failedTask = await _db.AgentTasks
            .Where(x => x.WorkflowRunId == run.Id && x.Status == AgentTaskStatus.Failed)
            .OrderByDescending(x => x.FinishedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (failedTask == null)
            return RetryWorkflowResult.NoRetryableTask;

        // Chạy lại từ đầu: bỏ checkpoint TRƯỚC khi đẩy task về Queued — worker poll mỗi 2 giây, xoá sau thì
        // nó có thể kịp nhặt task và chạy tiếp đúng hội thoại người dùng vừa bảo bỏ.
        if (restart)
            await _db.AgentRunCheckpoints.Where(x => x.TaskId == failedTask.Id).ExecuteDeleteAsync();

        // Đẩy lại task vào hàng đợi — worker sẽ tự tăng Attempt và báo "(lần thử N)".
        failedTask.Status = AgentTaskStatus.Queued;
        failedTask.Error = null;
        failedTask.Output = null;
        failedTask.StartedAt = null;
        failedTask.FinishedAt = null;

        // Khi đánh Failed, worker đã set CurrentStage = Failed. Khôi phục stage thật của bước này để
        // worker tra đúng MaxSteps và quyết định hand-off bước kế cho chuẩn.
        run.Status = WorkflowRunStatus.Queued;
        run.CurrentStage = ResolveStage(failedTask.Type, run.CurrentStage);
        run.FinishedAt = null;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Run/task đã bị một request Retry song song re-queue trước (Status là concurrency token) —
            // bên thua bỏ qua để không đẩy trùng; task kia đằng nào cũng đang được chạy lại.
            return RetryWorkflowResult.NoFailedRun;
        }

        return RetryWorkflowResult.Requeued;
    }

    private static WorkflowStageKey ResolveStage(AgentTaskType taskType, WorkflowStageKey fallback)
    {
        if (taskType == DeliveryPipeline.BugFixStep.TaskType)
            return DeliveryPipeline.BugFixStep.Stage;

        if (taskType == DeliveryPipeline.BuildFixStep.TaskType)
            return DeliveryPipeline.BuildFixStep.Stage;

        foreach (var step in DeliveryPipeline.Steps)
            if (step.TaskType == taskType)
                return step.Stage;

        // RequirementAnalysis (workflow "Write Requirement") không tra stage trong pipeline; giữ stage cũ.
        return fallback;
    }
}
