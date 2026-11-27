using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Mức suy luận (reasoning/thinking) xin cho MỌI lời gọi tới một model, đặt ở màn AI Models. Tồn tại vì model đời
/// mới tự BẬT suy luận khi không được bảo gì, và phần suy luận tính vào token output: qua Claude Code CLI, mọi lời
/// gọi đi với adaptive thinking ở effort <c>high</c> — ca JD Library 2026-11-26, Developer xuất 155k token output
/// cho một bản demo mà nội dung + script thật chỉ ~21k token. Muốn một agent suy luận ít hơn agent khác thì tạo
/// một dòng model riêng (cùng ModelId, khác mức) rồi gán cho agent đó.
/// Lưu xuống DB dạng chuỗi (tên enum) nên ĐỪNG đổi tên các giá trị đã ghi.
/// </summary>
public enum ReasoningEffortSetting
{
    /// <summary>Không gửi gì — model/CLI tự quyết theo mặc định của nó (hành vi trước khi có cột này).</summary>
    [Description("Model default (send nothing)")]
    Default,

    /// <summary>
    /// Tắt suy luận. OpenAI: <c>reasoning_effort: "none"</c>. Claude trên Vertex: <c>thinking: {type: "disabled"}</c>
    /// — CHỈ Haiku 5.5 nhận; Sonnet/Opus 5.5 trả 400, với chúng hãy dùng Low. Claude Code CLI không có cách tắt
    /// suy luận nên mức này đi ra là <c>--effort low</c>.
    /// </summary>
    [Description("Off (none / thinking disabled)")]
    None,

    [Description("Low")]
    Low,

    [Description("Medium")]
    Medium,

    [Description("High")]
    High,
}
