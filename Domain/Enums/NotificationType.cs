using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Loại thông báo in-app. Lưu xuống DB dạng chuỗi (tên enum) như các enum khác trong app, nên ĐỪNG đổi
/// tên các giá trị đã seed. Mỗi loại quyết định icon/màu hiển thị ở chuông thông báo.
/// </summary>
public enum NotificationType
{
    /// <summary>Một bước delivery đã xong và đang chờ người có quyền duyệt tại cổng trên Agent Dashboard.</summary>
    [Description("Chờ duyệt bước delivery")]
    GateAwaitingApproval,

    /// <summary>Cả workflow giao hàng đã hoàn tất (không còn bước kế).</summary>
    [Description("Workflow hoàn tất")]
    WorkflowCompleted,

    /// <summary>Workflow dừng vì lỗi — cần người xem lại.</summary>
    [Description("Workflow thất bại")]
    WorkflowFailed,

    /// <summary>Người yêu cầu đã NGHIỆM THU bản demo (POC) — đội delivery đẩy tiếp được các bước sau.</summary>
    [Description("Bản demo đã được nghiệm thu")]
    PocAccepted,

    /// <summary>Người yêu cầu đã RÚT nghiệm thu bản demo — lời "được rồi" trước đó không còn hiệu lực.</summary>
    [Description("Nghiệm thu bản demo đã bị rút")]
    PocAcceptanceWithdrawn
}
