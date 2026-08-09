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

    // Lượt hỏi GỘP: từ 2 tới 4 câu hỏi ĐỘC LẬP đặt cùng lúc, UI render thành một thẻ nhiều dòng (mỗi
    // dòng một câu hỏi + gợi ý bấm + ô tự nhập) và người dùng gửi cả cụm trong MỘT lượt. Rỗng ở lượt
    // hỏi một câu — khi đó Message chở câu hỏi và Suggestions chở gợi ý, đúng như trước.
    //
    // Hai đường tồn tại song song có chủ đích: lượt một câu là ca thường gặp nhất và cũng là ca BẮT
    // BUỘC của mọi câu hỏi đào sâu, nên nó giữ nguyên đường cũ (không đổi UX, không thêm rủi ro); lượt
    // gộp là đường mới. Parser tự gộp lại về đường một-câu nếu model chỉ trả đúng một câu trong
    // Questions, để không bao giờ có thẻ nhiều dòng chỉ có một dòng.
    public List<BAChatQuestion> Questions { get; set; } = new();

    // Lượt hỏi MỘT câu và câu đó là câu MỞ (xin lời kể / mô tả / giải thích): Suggestions phải RỖNG và UI
    // mời người dùng gõ vào ô nhập thay vì bấm chip. Xem BAChatQuestion.OpenEnded cho lý do đầy đủ — tóm
    // tắt: chip trên một câu mở chỉ trả lời được một MẨU của câu hỏi, mà bấm chip ở lượt một-câu là GỬI
    // NGAY, nên phần còn lại của câu chuyện rơi mất trong khi mọi tầng phía sau tin rằng câu đã được trả lời.
    // Chỉ có nghĩa ở lượt một câu; lượt gộp mang cờ này trên TỪNG phần tử của Questions.
    public bool OpenEnded { get; set; }

    // BA tự đánh giá đã khai thác đủ thông tin để soạn tài liệu hay chưa: true khi không còn câu hỏi
    // nào → UI bật nút "Write Requirement". Còn bất kỳ điểm nào cần hỏi thì để false (mặc định) để nút
    // ở trạng thái "chưa sẵn sàng". Đây là tín hiệu cho UI; bước sinh tài liệu vẫn có cổng readiness riêng.
    public bool Ready { get; set; }

    // Sơ đồ luồng nghiệp vụ chính (vai trò → hành động → kết quả) — CHỈ điền ở lượt mời bấm "Write
    // Requirement" (Ready = true) để user xác nhận trực quan trước khi tạo tài liệu. Rỗng ở các lượt hỏi.
    public List<FlowStep> FlowDiagram { get; set; } = new();
}
