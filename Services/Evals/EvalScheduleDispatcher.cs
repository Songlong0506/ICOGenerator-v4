using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Xử lý các <see cref="EvalSchedule"/> ĐẾN HẠN: mỗi lịch bật có NextRunAt &lt;= now thì tạo một
/// <see cref="EvalRun"/> Queued (EvalRunWorker nhặt chạy như run bấm tay) rồi dời NextRunAt tới
/// (now + IntervalHours). Tách khỏi worker nền để test được bằng Sqlite thật.
///
/// Nguyên tắc chống dồn đống: hạn LUÔN được dời tới kể cả khi lượt này bị BỎ QUA (run cũ của lịch chưa
/// xong / model đã tắt / không còn scenario khớp) — một lịch hỏng chỉ ghi log cảnh báo mỗi chu kỳ, không
/// retry dồn dập và không chặn các lịch khác.
/// </summary>
public class EvalScheduleDispatcher
{
    private readonly AppDbContext _db;
    private readonly ILogger<EvalScheduleDispatcher> _logger;

    public EvalScheduleDispatcher(AppDbContext db, ILogger<EvalScheduleDispatcher> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Xử lý mọi lịch đến hạn tại thời điểm <paramref name="now"/>; trả về số run đã tạo.</summary>
    public async Task<int> DispatchDueAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var due = await _db.EvalSchedules
            .Where(x => x.IsEnabled && x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
            return 0;

        var enqueued = 0;
        foreach (var schedule in due)
        {
            schedule.LastEnqueuedAt = now;
            schedule.NextRunAt = now.AddHours(Math.Max(1, schedule.IntervalHours));

            if (await TryEnqueueRunAsync(schedule, cancellationToken))
                enqueued++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return enqueued;
    }

    private async Task<bool> TryEnqueueRunAsync(EvalSchedule schedule, CancellationToken cancellationToken)
    {
        // Run trước của chính lịch này còn Queued/Running ⇒ bỏ qua lượt (đừng xếp chồng khi eval chậm hơn chu kỳ).
        var hasUnfinished = await _db.EvalRuns.AnyAsync(
            x => x.ScheduleId == schedule.Id && (x.Status == EvalRunStatus.Queued || x.Status == EvalRunStatus.Running),
            cancellationToken);
        if (hasUnfinished)
        {
            _logger.LogWarning("Lịch eval {Name} đến hạn nhưng run trước chưa xong — bỏ qua lượt này.", schedule.Name);
            return false;
        }

        // Snapshot tên model MỚI NHẤT lúc enqueue (tên có thể đã đổi từ khi tạo lịch); model tắt/xoá ⇒ bỏ qua lượt.
        var targetModel = await _db.AiModels.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == schedule.TargetModelId && x.IsActive, cancellationToken);
        var judgeModel = await _db.AiModels.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == schedule.JudgeModelId && x.IsActive, cancellationToken);
        if (targetModel == null || judgeModel == null)
        {
            _logger.LogWarning("Lịch eval {Name} đến hạn nhưng model mục tiêu/judge không còn hoạt động — bỏ qua lượt này.", schedule.Name);
            return false;
        }

        var scenarioQuery = _db.EvalScenarios.AsNoTracking().Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(schedule.PromptKey))
            scenarioQuery = scenarioQuery.Where(x => x.PromptKey == schedule.PromptKey);
        var scenarioCount = await scenarioQuery.CountAsync(cancellationToken);
        if (scenarioCount == 0)
        {
            _logger.LogWarning("Lịch eval {Name} đến hạn nhưng không còn scenario đang bật nào khớp bộ lọc — bỏ qua lượt này.", schedule.Name);
            return false;
        }

        _db.EvalRuns.Add(new EvalRun
        {
            Note = $"Lịch định kỳ: {schedule.Name}",
            PromptKey = string.IsNullOrWhiteSpace(schedule.PromptKey) ? null : schedule.PromptKey,
            TargetModelId = targetModel.Id,
            TargetModelName = targetModel.Name,
            JudgeModelId = judgeModel.Id,
            JudgeModelName = judgeModel.Name,
            ScenarioCount = scenarioCount,
            ScheduleId = schedule.Id,
            CreatedByUsername = schedule.CreatedByUsername
        });
        return true;
    }
}
