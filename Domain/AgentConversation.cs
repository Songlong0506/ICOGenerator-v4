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

    // JSON array (chuỗi) các câu hỏi của một lượt hỏi GỘP (BAChatQuestion[]: nhóm + câu hỏi + gợi ý +
    // cờ chọn-nhiều). Chỉ có ở lượt BA hỏi từ 2 câu trở lên; lượt hỏi một câu vẫn dùng Message +
    // Suggestions như cũ. Lưu lại (thay vì dựng lại từ Message) vì hai lý do:
    //   • reload trang phải render lại đúng thẻ hỏi, nếu không người dùng mất luôn các câu chưa trả lời;
    //   • Message của lượt gộp CHỈ là câu dẫn ngắn — không lưu cột này thì mọi reader transcript (bản đồ
    //     bao phủ, Product Brief, decision log) không hề thấy BA đã hỏi những gì, chỉ thấy câu trả lời.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? Questions { get; set; }

    // JSON array (chuỗi) BẢNG CỘT do BA đề xuất ở lượt đọc tài liệu nguồn (SourceColumnNote[]: file + tên
    // cột + ý nghĩa + cờ "có dùng"). Chỉ có ở lượt BA vừa đọc một bảng tính; null với mọi lượt khác.
    // Lưu lại vì đúng hai lý do của cột Questions: F5 giữa chừng mà bảng biến mất thì người dùng mất luôn
    // các dòng chưa tích, và Message của lượt đó không hề chứa danh sách cột — không có cột này thì mọi
    // reader transcript không biết BA đã đề xuất cách hiểu nào để người dùng gật/lắc.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? ColumnMap { get; set; }

    // JSON array (chuỗi) BẢNG PHÂN QUYỀN do BA đề xuất ở lượt chốt quyền cuối buổi (PermissionMatrixRow[]:
    // màn hình + chức năng + quyền của từng vai trò). Chỉ có ở đúng lượt đó; null với mọi lượt khác.
    // Lưu lại vì cùng hai lý do của cột ColumnMap: F5 giữa chừng mà bảng biến mất thì người dùng mất luôn
    // các ô chưa chọn (bảng này dài hơn bảng cột nhiều lần, mất là mất cả buổi tích), và Message của lượt
    // đó chỉ là câu dẫn — không có cột này thì mọi reader transcript không biết BA đã đề xuất quyền nào.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? PermissionMatrix { get; set; }

    // JSON array (chuỗi) các bước sơ đồ luồng nghiệp vụ (FlowStep[]) — CHỈ có ở lượt BA mời bấm "Write
    // Requirement" để user xác nhận luồng trực quan trước khi tạo tài liệu. Null với các lượt thường.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? FlowDiagram { get; set; }

    // JSON array (chuỗi) các file người dùng đính kèm ở lượt user này (ChatAttachment[]: id + tên +
    // cờ ảnh, trỏ về ProjectSourceFile) — để bubble hiển thị ảnh ngay trong hội thoại như ChatGPT/Claude.
    // File gốc vẫn sống ở "Tài liệu nguồn"; xóa nguồn thì bubble chỉ mất ảnh xem trước (id hỏng → ẩn).
    // Null với lượt không đính kèm. Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? Attachments { get; set; }

    public int TokenUsed { get; set; }

    // Thời điểm lượt bị LƯU TRỮ bởi "New Chat" (null = đang thuộc hội thoại hiện hành). Hội thoại là
    // nguồn gốc pháp lý của mọi tài liệu sinh ra sau đó nên không bao giờ xóa cứng: New Chat chỉ đóng
    // dấu ArchivedAt, mọi đường đọc (UI, memory, transcript) lọc ArchivedAt == null.
    public DateTime? ArchivedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
