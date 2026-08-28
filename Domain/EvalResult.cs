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
    /// SỐ phiên bản prompt (PromptTemplateVersion) đã dùng làm system prompt lúc chạy: null = nội dung
    /// FILE trong repo (không có bản DB active). Chỉ snapshot con số, KHÔNG FK và không giữ Guid — xoá
    /// lịch sử prompt không mất lịch sử điểm. Nhờ cột này, so hai run biết ngay mỗi run đo phiên bản
    /// prompt NÀO thay vì đoán theo thời điểm chạy.
    /// </summary>
    public int? PromptVersionNumber { get; set; }

    /// <summary>Điểm judge 1–5; null khi lời gọi target/judge lỗi hoặc judge trả về không parse được.</summary>
    public int? Score { get; set; }

    /// <summary>Giải thích của judge vì sao cho điểm đó.</summary>
    public string? JudgeReasoning { get; set; }

    /// <summary>
    /// Kết quả đối chiếu TỪNG dòng tiêu chí của scenario, JSON mảng <c>[{criterion, passed, note}]</c>;
    /// null khi judge (model cũ / bản judge prompt cũ) không trả phần này. Một điểm tổng 1–5 nói "có vấn
    /// đề", danh sách này nói "vấn đề nằm ở dòng tiêu chí nào" — nếu không có nó, mỗi lần điểm tụt lại
    /// phải đọc <see cref="JudgeReasoning"/> rồi đoán. Lưu nguyên JSON của judge (không dựng bảng con):
    /// đây là dữ liệu ĐỌC-KÈM-KẾT-QUẢ, không bao giờ bị truy vấn/lọc riêng.
    /// </summary>
    public string? CriteriaJson { get; set; }

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
