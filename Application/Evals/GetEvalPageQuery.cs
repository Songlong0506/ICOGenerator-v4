using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
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
    double? ScoreDelta,
    bool IsRegression,
    bool IsScheduled,
    long TotalTokens,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt);

public record EvalScheduleItemVm(
    Guid Id,
    string Name,
    string? PromptKey,
    Guid TargetModelId,
    string TargetModelName,
    Guid JudgeModelId,
    string JudgeModelName,
    int IntervalHours,
    double RegressionThreshold,
    bool IsEnabled,
    DateTime NextRunAt,
    DateTime? LastEnqueuedAt);

public record EvalModelOptionVm(Guid Id, string Name);

public record EvalPageVm(
    IReadOnlyList<EvalScenarioItemVm> Scenarios,
    IReadOnlyList<EvalScheduleItemVm> Schedules,
    IReadOnlyList<EvalRunItemVm> Runs,
    IReadOnlyList<string> PromptKeys,
    IReadOnlyList<EvalModelOptionVm> Models);

/// <summary>Dữ liệu trang Prompt Evals: golden set + các run gần nhất + danh mục prompt/model cho form.</summary>
public class GetEvalPageQuery
{
    private const int MaxRuns = 20;

    private readonly AppDbContext _db;
    private readonly EvalPromptCatalog _promptCatalog;

    public GetEvalPageQuery(AppDbContext db, EvalPromptCatalog promptCatalog)
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

        var schedules = await _db.EvalSchedules
            .AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .Select(x => new EvalScheduleItemVm(
                x.Id, x.Name, x.PromptKey, x.TargetModelId, x.TargetModelName, x.JudgeModelId, x.JudgeModelName,
                x.IntervalHours, x.RegressionThreshold, x.IsEnabled, x.NextRunAt, x.LastEnqueuedAt))
            .ToListAsync(cancellationToken);

        var runs = await _db.EvalRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxRuns)
            .Select(x => new EvalRunItemVm(
                x.Id, x.Note, x.PromptKey, x.TargetModelName, x.JudgeModelName, x.Status,
                x.ScenarioCount, x.CompletedCount, x.AverageScore, x.ScoreDelta, x.IsRegression,
                x.ScheduleId != null, x.TotalTokens, x.Error, x.CreatedAt, x.FinishedAt))
            .ToListAsync(cancellationToken);

        var models = await _db.AiModels
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new EvalModelOptionVm(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        return new EvalPageVm(scenarios, schedules, runs, _promptCatalog.PromptKeys, models);
    }
}
