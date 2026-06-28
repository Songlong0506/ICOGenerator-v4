using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Người dùng thấy kết quả một bước (vd kiến trúc, code, test) chưa đúng ý → hủy workflow
/// delivery đang chờ duyệt. Sau đó họ quay lại chat với BA, bổ sung requirement và bấm
/// "Write Requirement" → "Approve" để khởi tạo một workflow mới (phiên bản kế).
///
/// NGOẠI LỆ — cổng POC: POC sai nghĩa là requirement chưa đúng, mà đổi requirement là việc của
/// end-user (chat với BA → Approve lại), KHÔNG phải của TeamDev trên Agent Dashboard. Vì vậy ở
/// bước <see cref="WorkflowStageKey.PocPreview"/> use case từ chối ngay (trả
/// <see cref="RejectStageResult.PocGateNotRejectable"/>) — bảo vệ ở tầng server kể cả khi nút
/// "Từ chối" đã bị ẩn ở client.
/// </summary>
public enum RejectStageResult
{
    /// <summary>Đã hủy workflow đang chờ duyệt.</summary>
    Rejected,

    /// <summary>Không có run nào đang chờ duyệt để từ chối.</summary>
    NoWaitingRun,

    /// <summary>Đang ở cổng POC — TeamDev không được từ chối; đổi requirement là việc của user.</summary>
    PocGateNotRejectable
}

public class RejectStageUseCase
{
    private readonly AppDbContext _db;

    public RejectStageUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RejectStageResult> ExecuteAsync(Guid projectId, Guid? runId = null)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.ProjectId == projectId && x.Status == WorkflowRunStatus.WaitingForHuman);

        if (runId.HasValue)
            query = query.Where(x => x.Id == runId.Value);

        var run = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (run == null)
            return RejectStageResult.NoWaitingRun;

        // Cổng POC: từ chối = đổi requirement → việc của user, không phải TeamDev. Chặn ở server.
        if (run.CurrentStage == WorkflowStageKey.PocPreview)
            return RejectStageResult.PocGateNotRejectable;

        run.Status = WorkflowRunStatus.Canceled;
        run.FinishedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return RejectStageResult.Rejected;
    }
}
