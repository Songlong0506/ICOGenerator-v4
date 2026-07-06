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

    /// <summary>Điểm judge 1–5; null khi lời gọi target/judge lỗi hoặc judge trả về không parse được.</summary>
    public int? Score { get; set; }

    /// <summary>Giải thích của judge vì sao cho điểm đó.</summary>
    public string? JudgeReasoning { get; set; }

    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }

    public int TargetTokens { get; set; }
    public int JudgeTokens { get; set; }
    public long DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
