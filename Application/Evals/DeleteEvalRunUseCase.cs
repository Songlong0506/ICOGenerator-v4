using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public enum DeleteEvalRunResult
{
    Deleted,
    NotFound,

    /// <summary>Run đang Queued/Running — phải huỷ trước, xoá dưới chân worker sẽ làm nó nổ giữa chừng.</summary>
    StillRunning
}

/// <summary>
/// Xoá một EvalRun cùng toàn bộ kết quả của nó (FK Cascade). Bảng Runs là nơi làm việc hằng ngày: run
/// bấm nhầm, run lỗi hạ tầng, run thử nghiệm bỏ đi — không dọn được thì chúng đẩy các run có giá trị ra
/// khỏi tầm nhìn. Run đang chạy KHÔNG xoá được (huỷ trước đã).
/// </summary>
public class DeleteEvalRunUseCase
{
    private readonly AppDbContext _db;

    public DeleteEvalRunUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DeleteEvalRunResult> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.EvalRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
            return DeleteEvalRunResult.NotFound;

        if (run.Status is EvalRunStatus.Queued or EvalRunStatus.Running)
            return DeleteEvalRunResult.StillRunning;

        _db.EvalRuns.Remove(run);
        await _db.SaveChangesAsync(cancellationToken);
        return DeleteEvalRunResult.Deleted;
    }
}
