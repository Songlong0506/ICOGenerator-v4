using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public enum CancelEvalRunResult
{
    /// <summary>Run đang Queued: chưa ai chạy nên chốt Cancelled ngay tại đây.</summary>
    Cancelled,

    /// <summary>Run đang Running: đã đặt cờ, runner sẽ dừng ở ranh giới scenario kế tiếp.</summary>
    CancelRequested,

    NotFound,

    /// <summary>Run đã kết thúc (Completed/Failed/Cancelled) — không có gì để huỷ.</summary>
    AlreadyFinished
}

/// <summary>
/// Huỷ một EvalRun. Một run là N scenario × ít nhất 2 lời gọi LLM tính tiền thật, nên bấm nhầm model/bộ
/// lọc mà không có đường lùi là đốt tiền vô ích — đây chính là đường lùi đó.
/// <para>
/// Hai đường khác nhau theo trạng thái: <see cref="EvalRunStatus.Queued"/> chưa ai đụng tới nên chốt
/// Cancelled ngay; <see cref="EvalRunStatus.Running"/> thì chỉ ĐẶT CỜ <c>CancelRequestedAt</c> và để
/// <c>EvalRunnerService</c> tự dừng — worker chạy ở scope/DbContext khác, giật trạng thái từ bên ngoài
/// sẽ đụng độ với lần SaveChanges kế tiếp của nó.
/// </para>
/// </summary>
public class CancelEvalRunUseCase
{
    private readonly AppDbContext _db;

    public CancelEvalRunUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CancelEvalRunResult> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.EvalRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
            return CancelEvalRunResult.NotFound;

        if (run.Status is not (EvalRunStatus.Queued or EvalRunStatus.Running))
            return CancelEvalRunResult.AlreadyFinished;

        var now = DateTime.UtcNow;
        run.CancelRequestedAt ??= now;

        if (run.Status == EvalRunStatus.Running)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return CancelEvalRunResult.CancelRequested;
        }

        run.Status = EvalRunStatus.Cancelled;
        run.Error = "Đã huỷ trước khi chạy scenario nào.";
        run.FinishedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return CancelEvalRunResult.Cancelled;
    }
}
