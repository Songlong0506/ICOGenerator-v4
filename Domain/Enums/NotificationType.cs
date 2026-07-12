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

    /// <summary>Người dùng đã gửi một lượt góp ý (annotation) trên POC — đội Dev nên xem và xử lý.</summary>
    [Description("Có góp ý mới trên POC")]
    PocFeedbackSubmitted
}
