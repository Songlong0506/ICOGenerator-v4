using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

// Trạng thái gọn cho polling (JSON camelCase): tiến độ x/y + điểm trung bình tới thời điểm hiện tại.
public record EvalRunStatusVm(
    Guid Id,
    string Status,
    int ScenarioCount,
    int CompletedCount,
    double? AverageScore,
    long TotalTokens,
    decimal TotalCost,
    string? Error,
    DateTime? FinishedAt);

/// <summary>Tiến độ một EvalRun cho UI poll trong lúc worker chạy nền.</summary>
public class GetEvalRunStatusQuery
{
    private readonly AppDbContext _db;

    public GetEvalRunStatusQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<EvalRunStatusVm?> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return await _db.EvalRuns
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new EvalRunStatusVm(
                x.Id, x.Status.ToString(), x.ScenarioCount, x.CompletedCount, x.AverageScore, x.TotalTokens, x.TotalCost, x.Error, x.FinishedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
