using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Feedback;

/// <summary>
/// Dữ liệu cho màn hình Feedback: danh sách phản hồi (đã lọc theo quyền) cùng các cờ điều khiển UI.
/// </summary>
public record FeedbackPage(
    IReadOnlyList<FeedbackListItem> Items,
    bool CanManage,
    FeedbackStatus? StatusFilter,
    FeedbackType? TypeFilter,
    int TotalCount,
    long MaxFileBytes,
    int MaxFiles,
    IReadOnlyList<string> AllowedExtensions);
