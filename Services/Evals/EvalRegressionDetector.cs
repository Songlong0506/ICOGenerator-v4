using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Chốt chặn hồi quy prompt: khi một EvalRun vừa hoàn tất, so điểm với <b>baseline</b> — run Completed
/// gần nhất TRƯỚC đó có cùng model mục tiêu + cùng bộ lọc PromptKey (so sánh cùng-điều-kiện, đổi model
/// hay đổi bộ lọc thì không so chéo). Delta tính trên các scenario CHUNG cả hai run đều chấm được —
/// chính xác hơn so AverageScore thô vì bộ scenario có thể đã thêm/bớt giữa hai run.
/// <para>
/// Tụt từ ngưỡng trở lên ⇒ đánh dấu <see cref="EvalRun.IsRegression"/> + bắn thông báo cho người có
/// quyền EvalView. Ngưỡng lấy từ <see cref="EvalSchedule.RegressionThreshold"/> nếu run sinh từ lịch;
/// run bấm tay dùng ngưỡng mặc định cấu hình <c>Evals:RegressionThreshold</c> (0.5). Hợp đồng: chỉ ghi
/// lên entity đang track + Add notification — CALLER SaveChanges (atomic cùng trạng thái Completed).
/// Fail-open: lỗi ở đây không được làm gãy việc chốt run.
/// </para>
/// </summary>
public class EvalRegressionDetector
{
    public const double DefaultThreshold = 0.5;

    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;
    private readonly ILogger<EvalRegressionDetector> _logger;
    private readonly double _defaultThreshold;

    public EvalRegressionDetector(
        AppDbContext db,
        INotificationService notifications,
        IConfiguration configuration,
        ILogger<EvalRegressionDetector> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
        _defaultThreshold = configuration.GetValue("Evals:RegressionThreshold", DefaultThreshold);
    }

    /// <summary>
    /// So <paramref name="run"/> (đang track, vừa chuyển Completed, chưa SaveChanges) với baseline rồi
    /// ghi BaselineEvalRunId/ScoreDelta/IsRegression lên nó; tụt quá ngưỡng thì Add thông báo.
    /// </summary>
    public async Task ApplyAsync(EvalRun run, CancellationToken cancellationToken = default)
    {
        try
        {
            if (run.AverageScore == null)
                return; // Không scenario nào chấm được ⇒ không có gì để so.

            var baseline = await _db.EvalRuns.AsNoTracking()
                .Where(x => x.Id != run.Id
                            && x.Status == EvalRunStatus.Completed
                            && x.TargetModelId == run.TargetModelId
                            && x.PromptKey == run.PromptKey
                            && x.AverageScore != null
                            && x.CreatedAt < run.CreatedAt)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (baseline == null)
                return; // Run so sánh được đầu tiên của (model, bộ lọc) này — nó chính là baseline tương lai.

            var delta = await ComputeCommonScenarioDeltaAsync(run.Id, baseline.Id, cancellationToken);
            if (delta == null)
                return; // Không scenario chung nào cả hai cùng chấm được.

            run.BaselineEvalRunId = baseline.Id;
            run.ScoreDelta = delta;

            var threshold = await ResolveThresholdAsync(run.ScheduleId, cancellationToken);
            if (delta <= -threshold)
            {
                run.IsRegression = true;
                await _notifications.NotifyEvalRegressionAsync(run, delta.Value, threshold, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không so được hồi quy cho eval run {RunId} — run vẫn hoàn tất bình thường.", run.Id);
        }
    }

    // Delta = TB(run mới) − TB(baseline) trên giao của các scenario CÓ ĐIỂM ở cả hai run.
    private async Task<double?> ComputeCommonScenarioDeltaAsync(Guid runId, Guid baselineId, CancellationToken cancellationToken)
    {
        var rows = await _db.EvalResults.AsNoTracking()
            .Where(x => (x.EvalRunId == runId || x.EvalRunId == baselineId) && x.Score != null)
            .Select(x => new { x.EvalRunId, x.EvalScenarioId, x.Score })
            .ToListAsync(cancellationToken);

        var current = rows.Where(x => x.EvalRunId == runId).ToDictionary(x => x.EvalScenarioId, x => x.Score!.Value);
        var baseline = rows.Where(x => x.EvalRunId == baselineId).ToDictionary(x => x.EvalScenarioId, x => x.Score!.Value);

        var common = current.Keys.Where(baseline.ContainsKey).ToList();
        if (common.Count == 0)
            return null;

        var currentAvg = common.Average(id => (double)current[id]);
        var baselineAvg = common.Average(id => (double)baseline[id]);
        return Math.Round(currentAvg - baselineAvg, 2);
    }

    // Run sinh từ lịch dùng ngưỡng của lịch; lịch đã bị xoá / run bấm tay ⇒ ngưỡng mặc định từ config.
    private async Task<double> ResolveThresholdAsync(Guid? scheduleId, CancellationToken cancellationToken)
    {
        if (scheduleId is not Guid id)
            return _defaultThreshold;

        var scheduleThreshold = await _db.EvalSchedules.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => (double?)x.RegressionThreshold)
            .FirstOrDefaultAsync(cancellationToken);

        return scheduleThreshold is > 0 ? scheduleThreshold.Value : _defaultThreshold;
    }
}
