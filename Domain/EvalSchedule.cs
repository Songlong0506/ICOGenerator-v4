namespace ICOGenerator.Domain;

/// <summary>
/// Lịch chạy eval định kỳ: đến hạn (<see cref="NextRunAt"/>) thì EvalScheduleWorker tự tạo một
/// <see cref="EvalRun"/> Queued với model/bộ lọc của lịch — biến eval từ công cụ bấm tay thành lưới an
/// toàn tự động ("prompt/model có âm thầm tụt chất lượng không?"). Run sinh từ lịch mang
/// <see cref="EvalRun.ScheduleId"/> để lấy <see cref="RegressionThreshold"/> khi so với baseline.
/// Model tham chiếu bằng Guid + snapshot tên (KHÔNG FK — như EvalRun): xoá model không bị chặn, lịch
/// đến hạn mà model không còn thì bỏ qua lượt đó và ghi log.
/// </summary>
public class EvalSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tên lịch, vd "Đêm: bộ BA chat".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Chỉ chạy các scenario của MỘT template (null = mọi scenario đang bật) — như EvalRun.PromptKey.</summary>
    public string? PromptKey { get; set; }

    public Guid TargetModelId { get; set; }
    public string TargetModelName { get; set; } = string.Empty;

    public Guid JudgeModelId { get; set; }
    public string JudgeModelName { get; set; } = string.Empty;

    /// <summary>Chu kỳ chạy (giờ), tối thiểu 1 — khớp tinh thần "minimum interval hourly" của cron nội bộ.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Ngưỡng tụt điểm (thang 1–5) để coi là HỒI QUY: run mới thấp hơn baseline từ ngưỡng này trở lên
    /// (trên các scenario chung) ⇒ đánh dấu IsRegression + bắn thông báo cho người có quyền EvalView.
    /// </summary>
    public double RegressionThreshold { get; set; } = 0.5;

    /// <summary>Lịch tắt vẫn giữ cấu hình nhưng worker bỏ qua.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Hạn chạy kế tiếp; worker so với UtcNow. Được dời tới (now + IntervalHours) mỗi lần đến hạn.</summary>
    public DateTime NextRunAt { get; set; }

    /// <summary>Lần gần nhất lịch đến hạn và được xử lý (kể cả khi lượt đó bị bỏ qua vì run cũ chưa xong).</summary>
    public DateTime? LastEnqueuedAt { get; set; }

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
