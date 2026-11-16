using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Localization;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>
/// Huỷ một lượt kiểm toán trọng tài — cùng hai đường với <see cref="CancelEvalRunUseCase"/>: Queued chốt
/// Cancelled ngay, Running chỉ đặt cờ để runner dừng ở ranh giới lượt kế tiếp. Dùng chung
/// <see cref="CancelEvalRunResult"/> vì bốn kết cục y hệt.
/// </summary>
public class CancelJudgeAuditUseCase
{
    private readonly AppDbContext _db;

    public CancelJudgeAuditUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CancelEvalRunResult> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.JudgeAuditRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
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
        run.Error = Loc.T("CancelEvalRunUseCase.ExecuteAsync");
        run.FinishedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return CancelEvalRunResult.Cancelled;
    }
}
