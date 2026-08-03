using ICOGenerator.Data;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public record EvalResultItemVm(
    Guid Id,
    string ScenarioName,
    int? Score,
    bool IsSuccess,
    string? ErrorMessage,
    string Output,
    string? JudgeReasoning,
    int TargetTokens,
    int JudgeTokens,
    decimal TargetCost,
    decimal JudgeCost,
    long DurationMs,
    // Phiên bản prompt (Prompt Studio) đã dùng làm system prompt; null = nội dung file trong repo.
    int? PromptVersionNumber,
    // Đối chiếu từng dòng tiêu chí của scenario; rỗng với kết quả cũ (trước khi judge trả phần này).
    IReadOnlyList<EvalCriterionVerdict> Criteria);

public record EvalRunDetailVm(
    Guid Id,
    string? Note,
    string? PromptKey,
    string TargetModelName,
    string JudgeModelName,
    string Status,
    int ScenarioCount,
    int CompletedCount,
    double? AverageScore,
    long TotalTokens,
    decimal TotalCost,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    IReadOnlyList<EvalResultItemVm> Results);

/// <summary>Chi tiết một EvalRun (header + kết quả từng scenario) cho modal xem run.</summary>
public class GetEvalRunDetailQuery
{
    private readonly AppDbContext _db;

    public GetEvalRunDetailQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<EvalRunDetailVm?> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.EvalRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run == null)
            return null;

        // CriteriaJson bung ra thành danh sách ngay tại đây (không đẩy JSON thô cho JS tự đoán): parser đã
        // là nơi biết định dạng của judge, và kết quả cũ không có phần này chỉ đơn giản ra danh sách rỗng.
        var results = await _db.EvalResults
            .AsNoTracking()
            .Where(x => x.EvalRunId == runId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                Item = new EvalResultItemVm(
                    x.Id, x.ScenarioName, x.Score, x.IsSuccess, x.ErrorMessage,
                    x.Output, x.JudgeReasoning, x.TargetTokens, x.JudgeTokens, x.TargetCost, x.JudgeCost,
                    x.DurationMs, x.PromptVersionNumber, Array.Empty<EvalCriterionVerdict>()),
                x.CriteriaJson
            })
            .ToListAsync(cancellationToken);

        return new EvalRunDetailVm(
            run.Id, run.Note, run.PromptKey, run.TargetModelName, run.JudgeModelName, run.Status.ToString(),
            run.ScenarioCount, run.CompletedCount, run.AverageScore, run.TotalTokens, run.TotalCost, run.Error,
            run.CreatedAt, run.FinishedAt,
            results.Select(x => x.Item with { Criteria = EvalJudgeParser.ReadCriteriaJson(x.CriteriaJson) }).ToList());
    }
}
