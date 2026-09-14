namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Đường nào đã sinh ra một mục "checklist BA học được". Quyết định cách trang quản trị diễn giải bằng
/// chứng đi kèm mục đó ("người dùng tự nêu trong hội thoại" vs "ghi chú trên bản nháp Brief" vs "ghi chú
/// trên POC").
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
    /// DI SẢN — không còn đường nào sinh ra giá trị này: cổng xác nhận giả định giữa AI Design Spec và
    /// POC (cùng SpecAssumptionMemoryService) đã gỡ. Giá trị ở lại vì các bài học cũ trong
    /// <c>AgentChecklistItem</c> vẫn mang số 2 và trang quản trị vẫn phải đọc lên được nguồn của chúng.
    /// </summary>
    SpecAssumption = 2,

    /// <summary>
    /// Rút từ GHI CHÚ người dùng ghim lên bản nháp Product Brief, chắt lọc ở mốc họ bấm duyệt bản đó
    /// (ChecklistGapMemoryService). Mỗi ghi chú là một chỗ BA viết thiếu hoặc hiểu sai điều người dùng đã
    /// nói — bằng chứng trực tiếp, khác hẳn <see cref="Conversation"/> vốn phải SUY ra khoảng trống từ
    /// việc "người dùng tự nêu mà BA chưa hỏi".
    /// </summary>
    BriefNote = 3,

    /// <summary>
    /// Rút từ NHẬN XÉT người duyệt gõ ở nút "Yêu cầu chỉnh sửa" của một bước delivery, chắt lọc ở mốc họ
    /// bấm DUYỆT chính bước đó (StageRevisionMemoryService). Đây là đường học duy nhất KHÔNG dành cho BA:
    /// bước nào bị góp ý thì vai chạy bước đó (Technical Lead / Developer / Tester — tra
    /// <c>DeliveryPipeline.Steps</c>) là vai học được bài học.
    /// </summary>
    StageRevision = 4
}
