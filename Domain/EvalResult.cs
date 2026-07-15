namespace ICOGenerator.Domain;

/// <summary>
/// Kết quả MỘT scenario trong một <see cref="EvalRun"/>: output của model mục tiêu + điểm judge kèm lý do.
/// Scenario tham chiếu bằng Guid + snapshot tên (không FK) để xoá scenario không mất lịch sử run cũ;
/// so sánh hai run khớp scenario theo <see cref="EvalScenarioId"/>.
/// </summary>
public class EvalResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EvalRunId { get; set; }
    public EvalRun EvalRun { get; set; } = default!;

    public Guid EvalScenarioId { get; set; }
    public string ScenarioName { get; set; } = string.Empty;

    /// <summary>Trả lời của model mục tiêu cho UserInput của scenario.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// Phiên bản prompt (PromptTemplateVersion) đã dùng làm system prompt lúc chạy: null = nội dung
    /// FILE trong repo (không có bản DB active). Tham chiếu Guid + snapshot số phiên bản, KHÔNG FK
    /// (như EvalScenarioId) — xoá lịch sử prompt không mất lịch sử điểm. Nhờ cặp cột này, so hai run
    /// biết ngay mỗi run đo phiên bản prompt NÀO thay vì đoán theo thời điểm chạy.
    /// </summary>
    public Guid? PromptVersionId { get; set; }
    public int? PromptVersionNumber { get; set; }

    /// <summary>Điểm judge 1–5; null khi lời gọi target/judge lỗi hoặc judge trả về không parse được.</summary>
    public int? Score { get; set; }

    /// <summary>Giải thích của judge vì sao cho điểm đó.</summary>
    public string? JudgeReasoning { get; set; }

    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }

    public int TargetTokens { get; set; }
    public int JudgeTokens { get; set; }

    /// <summary>
    /// Chi phí USD của lời gọi model MỤC TIÊU / JUDGE, chốt NGAY lúc chạy theo đơn giá model tại thời điểm
    /// đó (LlmCost.Usd trên token prompt/completion). Snapshot thay vì tính lại lúc đọc vì — như tên model
    /// và số phiên bản prompt — model có thể bị xoá hoặc đổi giá sau này, run cũ vẫn phải đọc đúng chi phí đã
    /// tiêu. 0 khi model chưa đặt đơn giá (giống trang Usage) hoặc lời gọi lỗi.
    /// </summary>
    public decimal TargetCost { get; set; }
    public decimal JudgeCost { get; set; }

    public long DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
