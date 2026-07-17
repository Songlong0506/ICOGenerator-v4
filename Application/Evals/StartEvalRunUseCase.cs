using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public enum StartEvalRunResult
{
    Started,
    TargetModelNotFound,
    JudgeModelNotFound,
    NoActiveScenarios
}

/// <summary>
/// Tạo một EvalRun ở trạng thái Queued (EvalRunWorker nhặt chạy nền — một run là N scenario × 2 lời gọi
/// LLM, quá chậm cho một POST). Snapshot tên model ngay lúc tạo để lịch sử run đọc được kể cả khi model
/// sau này bị xoá.
/// </summary>
public class StartEvalRunUseCase
{
    private readonly AppDbContext _db;

    public StartEvalRunUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<StartEvalRunResult> ExecuteAsync(Guid targetModelId, Guid judgeModelId, string? promptKey, string? note, string? createdByUsername, CancellationToken cancellationToken = default)
    {
        var targetModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetModelId && x.IsActive, cancellationToken);
        if (targetModel == null)
            return StartEvalRunResult.TargetModelNotFound;

        var judgeModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == judgeModelId && x.IsActive, cancellationToken);
        if (judgeModel == null)
            return StartEvalRunResult.JudgeModelNotFound;

        promptKey = string.IsNullOrWhiteSpace(promptKey) ? null : promptKey.Trim();

        var scenarioQuery = _db.EvalScenarios.AsNoTracking().Where(x => x.IsActive);
        if (promptKey != null)
            scenarioQuery = scenarioQuery.Where(x => x.PromptKey == promptKey);

        var scenarioCount = await scenarioQuery.CountAsync(cancellationToken);
        if (scenarioCount == 0)
            return StartEvalRunResult.NoActiveScenarios;

        _db.EvalRuns.Add(new EvalRun
        {
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            PromptKey = promptKey,
            TargetModelId = targetModel.Id,
            TargetModelName = targetModel.ModelId,
            JudgeModelId = judgeModel.Id,
            JudgeModelName = judgeModel.ModelId,
            ScenarioCount = scenarioCount,
            CreatedByUsername = createdByUsername
        });
        await _db.SaveChangesAsync(cancellationToken);

        return StartEvalRunResult.Started;
    }
}
