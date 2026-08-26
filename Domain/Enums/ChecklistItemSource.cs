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
    PocFeedback = 1,

    /// <summary>
    /// Rút từ các giả định của AI Design Spec mà người dùng BÁC ở cổng xác nhận giả định
    /// (SpecAssumptionMemoryService). Bằng chứng sắc nhất trong ba đường: mỗi điểm bị bác là một câu hỏi
    /// buổi phỏng vấn lẽ ra phải hỏi, và nó tới SỚM hơn ghi chú POC — trước khi bản demo được dựng.
    /// </summary>
    SpecAssumption = 2
}
