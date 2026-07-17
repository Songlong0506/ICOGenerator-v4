using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public record EvalScenarioItemVm(
    Guid Id,
    string Name,
    string PromptKey,
    string UserInput,
    string Criteria,
    bool IsActive,
    DateTime CreatedAt);

public record EvalRunItemVm(
    Guid Id,
    string? Note,
    string? PromptKey,
    string TargetModelName,
    string JudgeModelName,
    EvalRunStatus Status,
    int ScenarioCount,
    int CompletedCount,
    double? AverageScore,
    long TotalTokens,
    decimal TotalCost,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt);

public record EvalModelOptionVm(Guid Id, string ModelId);

public record EvalPageVm(
    IReadOnlyList<EvalScenarioItemVm> Scenarios,
    IReadOnlyList<EvalRunItemVm> Runs,
    IReadOnlyList<string> PromptKeys,
    IReadOnlyList<EvalModelOptionVm> Models);

/// <summary>Dữ liệu trang Prompt Evals: golden set + các run gần nhất + danh mục prompt/model cho form.</summary>
public class GetEvalPageQuery
{
    private const int MaxRuns = 20;

    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _promptCatalog;

    public GetEvalPageQuery(AppDbContext db, PromptFileCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    public async Task<EvalPageVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var scenarios = await _db.EvalScenarios
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new EvalScenarioItemVm(x.Id, x.Name, x.PromptKey, x.UserInput, x.Criteria, x.IsActive, x.CreatedAt))
            .ToListAsync(cancellationToken);

        var runs = await _db.EvalRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxRuns)
            .Select(x => new EvalRunItemVm(
                x.Id, x.Note, x.PromptKey, x.TargetModelName, x.JudgeModelName, x.Status,
                x.ScenarioCount, x.CompletedCount, x.AverageScore, x.TotalTokens, x.TotalCost, x.Error, x.CreatedAt, x.FinishedAt))
            .ToListAsync(cancellationToken);

        var models = await _db.AiModels
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.ModelId)
            .Select(x => new EvalModelOptionVm(x.Id, x.ModelId))
            .ToListAsync(cancellationToken);

        return new EvalPageVm(scenarios, runs, _promptCatalog.PromptKeys, models);
    }
}
