using ICOGenerator.Services.Budget;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Các "núm vặn" của <see cref="ModelCallLoggingChatClient"/> cho MỘT đường thực thi. Gom thành một record
/// thay vì 6 tham số tùy chọn trên constructor: thêm núm mới sau này chỉ là thêm một property (mọi nơi
/// dựng client hiện có không phải sửa), và mỗi đường gọi tự đọc được nó bật/tắt gì.
///
/// Ba đường đang dùng:
/// <list type="bullet">
///   <item><b>Agent</b> (<c>AgentRunService</c>): <see cref="ThrowOnFailure"/> = true (lời gọi hỏng phải
///         KẾT THÚC run chứ không bị coi là câu trả lời cuối), có <see cref="OnProgress"/> +
///         <see cref="MaxSteps"/>/<see cref="HardCap"/> để hiện "bước 3/6", có <see cref="BudgetGuard"/>.</item>
///   <item><b>Chat/structured</b> (<c>LlmClient</c>): nuốt lỗi thành một <see cref="LlmCallResult"/> hỏng
///         (caller còn đường parse tay / hiện lỗi), có <see cref="BudgetGuard"/>.</item>
///   <item><b>Eval</b> (<c>EvalRunnerService</c>): nuốt lỗi, KHÔNG budget (không thuộc project nào).</item>
/// </list>
/// </summary>
/// <param name="RequestTimeoutSeconds">Deadline tổng cho một lời gọi — xem <see cref="LlmSettings.RequestTimeoutSeconds"/>.</param>
/// <param name="ThrowOnFailure">
/// <c>true</c>: lời gọi hỏng ném <see cref="InvalidOperationException"/> (kết thúc run).
/// <c>false</c>: nuốt lỗi, chỉ ghi nó lên <see cref="LlmCallResult"/> trả về qua <see cref="OnCompleted"/>.
/// </param>
public sealed record ModelCallOptions(int RequestTimeoutSeconds, bool ThrowOnFailure)
{
    /// <summary>
    /// Nhận <see cref="LlmCallResult"/> mà middleware vừa dựng (thành công hoặc hỏng-đã-nuốt), để caller
    /// cuối đường trả về luôn thay vì dựng lại. Đường agent bỏ qua vì nó đọc text đã stream.
    /// </summary>
    public Action<LlmCallResult>? OnCompleted { get; init; }

    /// <summary>Dòng tiến độ "đang suy nghĩ…" / "lời gọi thất bại" đẩy lên UI (kind, message, detail).</summary>
    public Action<string, string, string?>? OnProgress { get; init; }

    /// <summary>Ngân sách bước KỲ VỌNG, chỉ để hiển thị "bước 3/6" trong dòng tiến độ. 0 = chỉ hiện số bước.</summary>
    public int MaxSteps { get; init; }

    /// <summary>Trần cứng số bước, chỉ để hiển thị khi đã vượt <see cref="MaxSteps"/>.</summary>
    public int HardCap { get; init; }

    /// <summary>
    /// Cầu dao ngân sách, hỏi TRƯỚC mỗi round-trip. <c>null</c> = không kiểm tra (eval, unit test).
    /// </summary>
    public IBudgetGuard? BudgetGuard { get; init; }
}
