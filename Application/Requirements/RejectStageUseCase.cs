using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Người dùng thấy kết quả một bước (vd POC) chưa đúng ý → hủy workflow delivery
/// đang chờ duyệt. Sau đó họ quay lại chat với BA, bổ sung requirement và bấm
/// "Write Requirement" → "Approve" để khởi tạo một workflow mới (phiên bản kế).
/// </summary>
public class RejectStageUseCase
{
    private readonly AppDbContext _db;

    public RejectStageUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> ExecuteAsync(Guid projectId, Guid? runId = null)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return false;

        run.Status = WorkflowRunStatus.Canceled;
        run.FinishedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }
}
