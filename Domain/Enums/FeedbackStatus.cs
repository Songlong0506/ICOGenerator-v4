using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Trạng thái xử lý một phản hồi (do người có quyền FeedbackManage triage). Lưu dạng chuỗi trong DB.
/// </summary>
public enum FeedbackStatus
{
    [Description("Mới")]
    New = 1,
    [Description("Đang xử lý")]
    InProgress = 2,
    [Description("Đã xử lý")]
    Resolved = 3,
    [Description("Đã đóng")]
    Closed = 4
}
