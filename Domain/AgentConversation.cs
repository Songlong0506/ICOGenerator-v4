namespace ICOGenerator.Domain;

public class AgentConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;

    public string Role { get; set; } = "assistant"; // user / assistant

    public string Message { get; set; } = string.Empty;

    // JSON array (chuỗi) các đáp án gợi ý cho lượt hỏi của BA, để UI render thành "chip" bấm chọn
    // (giống plan mode). Null/không có nghĩa là lượt này không kèm gợi ý. Trước UI là mục đích DUY NHẤT,
    // nhưng gợi ý cũng là NGỮ CẢNH: câu trả lời tham chiếu ("Cả hai mục tiêu trên") vô nghĩa nếu reader
    // không thấy các option đã đưa ra. Vì vậy khi dựng ngữ cảnh gửi LLM, các reader render qua
    // ConversationTurnRenderer để đính kèm danh sách này (Message vẫn giữ nguyên phần text thuần cho UI).
    public string? Suggestions { get; set; }

    // true khi lượt hỏi này cho phép CHỌN NHIỀU đáp án gợi ý cùng lúc (vd "gồm những vai trò nào?").
    // UI đổi chip sang chế độ toggle + nút gửi; các đáp án đã chọn được gửi thành MỘT tin nhắn.
    // Cờ do model trả trong JSON {multiSelect} và được lưu lại để reload trang vẫn render đúng chế độ.
    public bool SuggestionsMultiSelect { get; set; }

    // JSON array (chuỗi) các bước sơ đồ luồng nghiệp vụ (FlowStep[]) — CHỈ có ở lượt BA mời bấm "Write
    // Requirement" để user xác nhận luồng trực quan trước khi tạo tài liệu. Null với các lượt thường.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? FlowDiagram { get; set; }

    public int TokenUsed { get; set; }

    // Thời điểm lượt bị LƯU TRỮ bởi "New Chat" (null = đang thuộc hội thoại hiện hành). Hội thoại là
    // nguồn gốc pháp lý của mọi tài liệu sinh ra sau đó nên không bao giờ xóa cứng: New Chat chỉ đóng
    // dấu ArchivedAt, mọi đường đọc (UI, memory, transcript) lọc ArchivedAt == null.
    public DateTime? ArchivedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
