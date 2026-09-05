namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Đường nào đã sinh ra một mục "checklist BA học được". Quyết định cách trang quản trị diễn giải bằng
/// chứng đi kèm mục đó ("người dùng tự nêu trong hội thoại" vs "ghi chú trên bản nháp Brief" vs "ghi chú
/// trên POC" vs "giả định bị bác").
/// </summary>
public enum ChecklistItemSource
{
    /// <summary>
    /// Rút từ hội thoại phỏng vấn, ở mốc duyệt Product Brief mà người dùng KHÔNG ghim ghi chú nào
    /// (ChecklistGapMemoryService). Đây là lưới đỡ: Brief đúng ngay có thể vì người dùng đã tự khai đủ
    /// những gì BA quên hỏi — bộ câu hỏi vẫn thiếu, chỉ là không ai phàn nàn. Chạy đúng một lần mỗi dự án.
    /// </summary>
    Conversation = 0,

    /// <summary>Rút từ ghi chú người dùng ghim trên POC và đã gửi cho Developer sửa (PocFeedbackMemoryService).</summary>
    PocFeedback = 1,

    /// <summary>
    /// Rút từ các giả định của AI Design Spec mà người dùng BÁC ở cổng xác nhận giả định
    /// (SpecAssumptionMemoryService). Bằng chứng sắc nhất trong ba đường: mỗi điểm bị bác là một câu hỏi
    /// buổi phỏng vấn lẽ ra phải hỏi, và nó tới SỚM hơn ghi chú POC — trước khi bản demo được dựng.
    /// </summary>
    SpecAssumption = 2,

    /// <summary>
    /// Rút từ GHI CHÚ người dùng ghim lên bản nháp Product Brief, chắt lọc ở mốc họ bấm duyệt bản đó
    /// (ChecklistGapMemoryService). Mỗi ghi chú là một chỗ BA viết thiếu hoặc hiểu sai điều người dùng đã
    /// nói — bằng chứng trực tiếp, khác hẳn <see cref="Conversation"/> vốn phải SUY ra khoảng trống từ
    /// việc "người dùng tự nêu mà BA chưa hỏi".
    /// </summary>
    BriefNote = 3
}
