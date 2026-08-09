namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Đường nào đã sinh ra một mục "checklist BA học được". Quyết định cách trang quản trị diễn giải bằng
/// chứng đi kèm mục đó ("người dùng tự nêu trong hội thoại" vs "ghi chú trên POC").
/// </summary>
public enum ChecklistItemSource
{
    /// <summary>Rút từ hội thoại phỏng vấn của một dự án vừa sinh tài liệu (ChecklistGapMemoryService).</summary>
    Conversation = 0,

    /// <summary>Rút từ ghi chú người dùng ghim trên POC và đã gửi cho Developer sửa (PocFeedbackMemoryService).</summary>
    PocFeedback = 1
}
