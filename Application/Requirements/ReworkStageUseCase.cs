using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ReworkStageResult
{
    Queued,          // đã enqueue task sửa lỗi (BugFix) cho Developer
    NotReworkable,   // bước đang chờ duyệt không hỗ trợ gửi lại sửa lỗi
    NoPendingStage,  // không có workflow nào đang chờ duyệt
    MissingAgent     // không tìm thấy agent cho vai sửa lỗi
}

/// <summary>
/// Vòng lặp chất lượng: tại cổng duyệt của một bước có cấu hình <see cref="ReworkSpec"/>
/// (vd Testing), thay vì duyệt-hoàn-tất, người dùng có thể "gửi lại" để vai sửa lỗi
/// (Developer) chạy một task <see cref="AgentTaskType.BugFix"/>. Sau khi sửa xong,
/// <see cref="Services.Workflows.AgentTaskWorker"/> tự chạy lại chính bước đó để xác minh —
/// CurrentStage không đổi nên workflow lại dừng ở đúng cổng cũ.
/// </summary>
public class ReworkStageUseCase
{
    private readonly AppDbContext _db;

    public ReworkStageUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ReworkStageResult> ExecuteAsync(Guid projectId, Guid? runId = null)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return ReworkStageResult.NoPendingStage;

        var rework = DeliveryPipeline.Find(run.CurrentStage)?.Rework;
        if (rework == null)
            return ReworkStageResult.NotReworkable;

        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.RoleKey == rework.Role);
        if (agent == null)
            return ReworkStageResult.MissingAgent;

        // Input = output của task vừa hoàn tất (vd báo cáo test) — chính là danh sách lỗi cần sửa.
        var lastOutput = await _db.AgentTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == AgentTaskStatus.Completed)
            .OrderByDescending(t => t.FinishedAt)
            .Select(t => t.Output)
            .FirstOrDefaultAsync();

        _db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = run.Id,
            ProjectId = projectId,
            AgentId = agent.Id,
            Type = rework.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = rework.Title,
            Input = lastOutput ?? string.Empty
        });

        // CurrentStage giữ nguyên (vẫn là bước có rework); worker chạy BugFix rồi tự chạy lại bước này.
        run.Status = WorkflowRunStatus.Queued;

        await _db.SaveChangesAsync();
        return ReworkStageResult.Queued;
    }
}
