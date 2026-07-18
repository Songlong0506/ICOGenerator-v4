namespace ICOGenerator.Contracts.Requirements;

// Kết quả parse một lượt trả lời của BA ở chế độ trò chuyện: phần text hiển thị (Message) và
// danh sách đáp án gợi ý (Suggestions) để UI render thành "chip" cho người dùng bấm chọn.
// Suggestions có thể rỗng khi BA không đặt câu hỏi (vd: tóm tắt/xác nhận hoặc nhắc bấm "Write Requirement").
public class BAChatReply
{
    public string Message { get; set; } = "";

    public List<string> Suggestions { get; set; } = new();

    // true khi câu hỏi của lượt này cho phép CHỌN NHIỀU đáp án gợi ý cùng lúc (vd "gồm những vai trò
    // nào?") — UI đổi chip sang chế độ toggle + nút gửi. Mặc định false (chọn một, gửi ngay).
    public bool MultiSelect { get; set; }

    // BA tự đánh giá đã khai thác đủ thông tin để soạn tài liệu hay chưa: true khi không còn câu hỏi
    // nào → UI bật nút "Write Requirement". Còn bất kỳ điểm nào cần hỏi thì để false (mặc định) để nút
    // ở trạng thái "chưa sẵn sàng". Đây là tín hiệu cho UI; bước sinh tài liệu vẫn có cổng readiness riêng.
    public bool Ready { get; set; }

    // Sơ đồ luồng nghiệp vụ chính (vai trò → hành động → kết quả) — CHỈ điền ở lượt mời bấm "Write
    // Requirement" (Ready = true) để user xác nhận trực quan trước khi tạo tài liệu. Rỗng ở các lượt hỏi.
    public List<FlowStep> FlowDiagram { get; set; } = new();
}
