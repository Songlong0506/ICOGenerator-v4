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
    /// Một lượt (<see cref="Enums.EvalScenarioKind.Prompt"/>) hay cả cuộc PHỎNG VẤN mô phỏng
    /// (<see cref="Enums.EvalScenarioKind.Interview"/> — <see cref="UserInput"/> khi đó là hồ sơ persona
    /// để một model đóng vai người dùng nghiệp vụ). Mặc định Prompt: scenario cũ chạy y như trước.
    /// </summary>
    public Enums.EvalScenarioKind Kind { get; set; } = Enums.EvalScenarioKind.Prompt;

    /// <summary>
    /// Đường dẫn template prompt dưới /Prompts (vd "BusinessAnalyst/requirement-chat.v3.md"). Nội dung HIỆN HÀNH của
    /// file được dùng làm system prompt lúc chạy — nên cùng bộ scenario đo được các phiên bản prompt khác nhau.
    /// </summary>
    public string PromptKey { get; set; } = string.Empty;

    /// <summary>
    /// Với <see cref="Enums.EvalScenarioKind.Prompt"/>: đầu vào mô phỏng gửi kèm system prompt.
    /// Với <see cref="Enums.EvalScenarioKind.Interview"/>: HỒ SƠ PERSONA — bài toán, vai trò, quy trình,
    /// con số mà "người dùng" giả lập biết và sẽ chỉ tiết lộ khi BA hỏi đúng chỗ.
    /// </summary>
    public string UserInput { get; set; } = string.Empty;

    /// <summary>Tiêu chí chấm (bullet list) mà judge dùng để cho điểm 1–5.</summary>
    public string Criteria { get; set; } = string.Empty;

    /// <summary>Scenario tắt vẫn giữ lịch sử kết quả nhưng không được chạy trong run mới.</summary>
    public bool IsActive { get; set; } = true;

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
