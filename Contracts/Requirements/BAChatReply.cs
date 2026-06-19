namespace ICOGenerator.Contracts.Requirements;

// Kết quả parse một lượt trả lời của BA ở chế độ trò chuyện: phần text hiển thị (Message) và
// danh sách đáp án gợi ý (Suggestions) để UI render thành "chip" cho người dùng bấm chọn.
// Suggestions có thể rỗng khi BA không đặt câu hỏi (vd: tóm tắt/xác nhận hoặc nhắc bấm "Write Requirement").
public class BAChatReply
{
    public string Message { get; set; } = "";

    public List<string> Suggestions { get; set; } = new();
}
