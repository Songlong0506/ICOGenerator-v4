using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Mức suy luận (reasoning/thinking) xin cho MỌI lời gọi tới một model. Tồn tại vì model đời mới tự BẬT suy luận
/// khi không được bảo gì: <c>claude-haiku-5-5</c> mặc định adaptive thinking ở effort <c>medium</c> và phần suy
/// luận trả về RỖNG nhưng vẫn tính vào <c>max_tokens</c> — ca thật là một lượt dựng POC tiêu sạch 60k token vào
/// suy luận ẩn, Response trống trơn. <c>gpt-6-luna</c> thì từ chối tool khi đang suy luận.
/// Lưu xuống DB dạng chuỗi (tên enum) nên ĐỪNG đổi tên các giá trị đã ghi.
/// </summary>
public enum ReasoningEffortSetting
{
    /// <summary>Không gửi gì — model tự quyết theo mặc định của nó (hành vi trước khi có cột này).</summary>
    [Description("Mặc định của model (không gửi)")]
    Default,

    /// <summary>
    /// Tắt suy luận. OpenAI: <c>reasoning_effort: "none"</c>. Claude: <c>thinking: {type: "disabled"}</c> — CHỈ
    /// Haiku 5.5 (ở effort ≤ high) nhận; Sonnet 5.5 / Opus 5.5 trả 400, với chúng hãy dùng Low.
    /// </summary>
    [Description("Tắt (none / thinking disabled)")]
    None,

    [Description("Low")]
    Low,

    [Description("Medium")]
    Medium,

    [Description("High")]
    High,
}
