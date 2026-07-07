using ICOGenerator.Data;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>
/// Sửa một lịch eval định kỳ (tên, model, bộ lọc prompt, chu kỳ, ngưỡng tụt, bật/tắt). Hạn chạy kế tiếp
/// được tính lại = now + IntervalHours ("lịch đếm lại từ lúc sửa") — dễ đoán hơn là cố nội suy hạn cũ.
/// </summary>
public class UpdateEvalScheduleUseCase
{
    private readonly AppDbContext _db;
    private readonly EvalPromptCatalog _promptCatalog;

    public UpdateEvalScheduleUseCase(AppDbContext db, EvalPromptCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    public async Task<SaveEvalScheduleResult> ExecuteAsync(
        Guid id, string? name, Guid targetModelId, Guid judgeModelId, string? promptKey,
        int intervalHours, double regressionThreshold, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (!EvalScheduleRules.IsValid(name, intervalHours, regressionThreshold))
            return SaveEvalScheduleResult.InvalidInput;

        promptKey = string.IsNullOrWhiteSpace(promptKey) ? null : promptKey.Trim();
        if (promptKey != null && !_promptCatalog.Exists(promptKey))
            return SaveEvalScheduleResult.UnknownPromptKey;

        var schedule = await _db.EvalSchedules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (schedule == null)
            return SaveEvalScheduleResult.NotFound;

        var targetModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetModelId && x.IsActive, cancellationToken);
        if (targetModel == null)
            return SaveEvalScheduleResult.TargetModelNotFound;

        var judgeModel = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == judgeModelId && x.IsActive, cancellationToken);
        if (judgeModel == null)
            return SaveEvalScheduleResult.JudgeModelNotFound;

        schedule.Name = name!.Trim();
        schedule.PromptKey = promptKey;
        schedule.TargetModelId = targetModel.Id;
        schedule.TargetModelName = targetModel.Name;
        schedule.JudgeModelId = judgeModel.Id;
        schedule.JudgeModelName = judgeModel.Name;
        schedule.IntervalHours = intervalHours;
        schedule.RegressionThreshold = regressionThreshold;
        schedule.IsEnabled = isEnabled;
        schedule.NextRunAt = DateTime.UtcNow.AddHours(intervalHours);
        schedule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return SaveEvalScheduleResult.Saved;
    }
}
