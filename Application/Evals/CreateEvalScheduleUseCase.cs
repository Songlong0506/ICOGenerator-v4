using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>
/// Tạo một lịch chạy eval định kỳ. Hạn chạy ĐẦU TIÊN = now + IntervalHours (không chạy ngay lúc tạo —
/// tạo lịch không được âm thầm đốt token; cần đo ngay thì bấm "Chạy eval" thủ công như trước).
/// </summary>
public class CreateEvalScheduleUseCase
{
    private readonly AppDbContext _db;
    private readonly EvalPromptCatalog _promptCatalog;

    public CreateEvalScheduleUseCase(AppDbContext db, EvalPromptCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    public async Task<SaveEvalScheduleResult> ExecuteAsync(
        string? name, Guid targetModelId, Guid judgeModelId, string? promptKey,
        int intervalHours, double regressionThreshold, string? createdByUsername,
        CancellationToken cancellationToken = default)
    {
        if (!EvalScheduleRules.IsValid(name, intervalHours, regressionThreshold))
            return SaveEvalScheduleResult.InvalidInput;

        promptKey = string.IsNullOrWhiteSpace(promptKey) ? null : promptKey.Trim();
        if (promptKey != null && !_promptCatalog.Exists(promptKey))
            return SaveEvalScheduleResult.UnknownPromptKey;

        var targetModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetModelId && x.IsActive, cancellationToken);
        if (targetModel == null)
            return SaveEvalScheduleResult.TargetModelNotFound;

        var judgeModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == judgeModelId && x.IsActive, cancellationToken);
        if (judgeModel == null)
            return SaveEvalScheduleResult.JudgeModelNotFound;

        _db.EvalSchedules.Add(new EvalSchedule
        {
            Name = name!.Trim(),
            PromptKey = promptKey,
            TargetModelId = targetModel.Id,
            TargetModelName = targetModel.Name,
            JudgeModelId = judgeModel.Id,
            JudgeModelName = judgeModel.Name,
            IntervalHours = intervalHours,
            RegressionThreshold = regressionThreshold,
            NextRunAt = DateTime.UtcNow.AddHours(intervalHours),
            CreatedByUsername = createdByUsername
        });
        await _db.SaveChangesAsync(cancellationToken);

        return SaveEvalScheduleResult.Saved;
    }
}
