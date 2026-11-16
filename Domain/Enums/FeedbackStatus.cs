namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Trạng thái xử lý một phản hồi (do người có quyền FeedbackManage triage). Lưu dạng chuỗi trong DB.
/// </summary>
public enum FeedbackStatus
{
    New = 1,
    InProgress = 2,
    Resolved = 3,
    Closed = 4
}
