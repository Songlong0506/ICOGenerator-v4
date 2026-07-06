namespace ICOGenerator.Domain;

/// <summary>
/// Một tình huống trong "golden set" để đánh giá prompt: cho một template prompt (<see cref="PromptKey"/>)
/// và một đầu vào mô phỏng người dùng (<see cref="UserInput"/>), model phải trả lời đạt các
/// <see cref="Criteria"/>. Một EvalRun chạy lại toàn bộ scenario đang bật để đo "đổi prompt/model có
/// làm chất lượng lên hay xuống" thay vì sửa-rồi-cầu-nguyện.
/// </summary>
public class EvalScenario
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn template prompt dưới /Prompts (vd "BA/requirement-chat.v3.md"). Nội dung HIỆN HÀNH của
    /// file được dùng làm system prompt lúc chạy — nên cùng bộ scenario đo được các phiên bản prompt khác nhau.
    /// </summary>
    public string PromptKey { get; set; } = string.Empty;

    /// <summary>Đầu vào mô phỏng (tin nhắn/transcript người dùng) gửi kèm system prompt.</summary>
    public string UserInput { get; set; } = string.Empty;

    /// <summary>Tiêu chí chấm (bullet list) mà judge dùng để cho điểm 1–5.</summary>
    public string Criteria { get; set; } = string.Empty;

    /// <summary>Scenario tắt vẫn giữ lịch sử kết quả nhưng không được chạy trong run mới.</summary>
    public bool IsActive { get; set; } = true;

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
