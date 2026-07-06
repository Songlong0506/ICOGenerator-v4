using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một lượt chạy eval: mọi <see cref="EvalScenario"/> đang bật (lọc theo <see cref="PromptKey"/> nếu có)
/// được chạy với model mục tiêu rồi được model judge chấm 1–5 theo tiêu chí của scenario. Chạy NỀN bởi
/// EvalRunWorker (tạo ở trạng thái Queued); UI poll tiến độ qua <see cref="CompletedCount"/>.
/// Model tham chiếu bằng Guid + snapshot tên (KHÔNG khai FK tới AiModels — như AgentModelCallLog — để
/// việc xoá một AI model không bị chặn bởi lịch sử eval và run cũ vẫn đọc được tên model).
/// </summary>
public class EvalRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Ghi chú của người chạy, vd "sau khi sửa requirement-chat v3".</summary>
    public string? Note { get; set; }

    /// <summary>Chỉ chạy các scenario của MỘT template (null = mọi scenario đang bật).</summary>
    public string? PromptKey { get; set; }

    public Guid TargetModelId { get; set; }
    public string TargetModelName { get; set; } = string.Empty;

    public Guid JudgeModelId { get; set; }
    public string JudgeModelName { get; set; } = string.Empty;

    public EvalRunStatus Status { get; set; } = EvalRunStatus.Queued;

    /// <summary>Số scenario sẽ chạy (chốt lúc tạo run để UI hiển thị x/y ổn định).</summary>
    public int ScenarioCount { get; set; }

    public int CompletedCount { get; set; }

    /// <summary>Điểm judge trung bình (1–5) trên các scenario chấm được; null khi chưa xong/không chấm được.</summary>
    public double? AverageScore { get; set; }

    /// <summary>Tổng token của cả lời gọi target lẫn judge trong run.</summary>
    public long TotalTokens { get; set; }

    /// <summary>Lỗi mức RUN (model bị xoá, worker gián đoạn...); lỗi từng scenario nằm trên EvalResult.</summary>
    public string? Error { get; set; }

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public List<EvalResult> Results { get; set; } = new();
}
