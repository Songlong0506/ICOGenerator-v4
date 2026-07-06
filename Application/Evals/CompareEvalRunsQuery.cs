using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public record EvalRunHeaderVm(Guid Id, string? Note, string TargetModelName, double? AverageScore, DateTime CreatedAt);

// Một dòng so sánh: điểm của cùng scenario ở hai run (null = run đó không chạy/không chấm được scenario này).
public record EvalCompareRowVm(string ScenarioName, int? ScoreA, int? ScoreB, int? Delta);

public record EvalCompareVm(EvalRunHeaderVm RunA, EvalRunHeaderVm RunB, IReadOnlyList<EvalCompareRowVm> Rows);

/// <summary>
/// So sánh hai EvalRun theo TỪNG scenario (khớp bằng EvalScenarioId — đổi tên scenario không làm lệch
/// cặp): trả lời câu "đổi prompt/model xong, tình huống nào lên điểm, tình huống nào tụt".
/// </summary>
public class CompareEvalRunsQuery
{
    private readonly AppDbContext _db;

    public CompareEvalRunsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<EvalCompareVm?> ExecuteAsync(Guid runAId, Guid runBId, CancellationToken cancellationToken = default)
    {
        var runs = await _db.EvalRuns
            .AsNoTracking()
            .Where(x => x.Id == runAId || x.Id == runBId)
            .Select(x => new EvalRunHeaderVm(x.Id, x.Note, x.TargetModelName, x.AverageScore, x.CreatedAt))
            .ToListAsync(cancellationToken);

        var runA = runs.FirstOrDefault(x => x.Id == runAId);
        var runB = runs.FirstOrDefault(x => x.Id == runBId);
        if (runA == null || runB == null)
            return null;

        var results = await _db.EvalResults
            .AsNoTracking()
            .Where(x => x.EvalRunId == runAId || x.EvalRunId == runBId)
            .Select(x => new { x.EvalRunId, x.EvalScenarioId, x.ScenarioName, x.Score, x.CreatedAt })
            .ToListAsync(cancellationToken);

        var rows = results
            .GroupBy(x => x.EvalScenarioId)
            .Select(g =>
            {
                var a = g.FirstOrDefault(x => x.EvalRunId == runAId);
                var b = g.FirstOrDefault(x => x.EvalRunId == runBId);
                var name = (b ?? a)!.ScenarioName;
                int? delta = a?.Score is int sa && b?.Score is int sb ? sb - sa : null;
                return new { Row = new EvalCompareRowVm(name, a?.Score, b?.Score, delta), Order = g.Min(x => x.CreatedAt) };
            })
            .OrderBy(x => x.Order)
            .Select(x => x.Row)
            .ToList();

        return new EvalCompareVm(runA, runB, rows);
    }
}
