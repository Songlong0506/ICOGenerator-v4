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

    // Lượt này là NHỊP TÓM TẮT KIỂM CHỨNG: BA phát lại cách mình hiểu rồi xin một tiếng gật. Model tự khai,
    // như OpenEnded và MultiSelect — cơ chế chỉ BÙ bộ hai chip ["Đúng rồi, tiếp tục", "Tôi muốn sửa lại"]
    // khi model quên kèm chip, chứ không xoá gì của lượt.
    //
    // Trước đây cờ này được ĐOÁN bằng một bảng cụm từ tiếng Việt ("tóm tắt lại", "mình hiểu đúng"…) cộng
    // một dấu hỏi. Cùng lý do đã gỡ ShapeAnswer/LooksOpenEnded khỏi parser: bảng cụm từ không bao giờ phủ
    // hết, mà bắt quá tay thì gắn chip xác nhận vào một câu hỏi khai thác thật — biến một câu đang đào sâu
    // thành một cái gật. Chỗ dạy nhận ra nhịp này là prompt (requirement-chat.v4.md), và cái giá của việc
    // bỏ phanh — model quên cờ nên lượt đó không có chip — là cái giá đã biết: người dùng gõ tay một câu.
    public bool SummaryCheck { get; set; }

    // BA tự đánh giá đã khai thác đủ thông tin để soạn tài liệu hay chưa: true khi không còn câu hỏi
    // nào → UI bật nút "Write Requirement". Còn bất kỳ điểm nào cần hỏi thì để false (mặc định) để nút
    // ở trạng thái "chưa sẵn sàng". Đây là tín hiệu cho UI; bước sinh tài liệu vẫn có cổng readiness riêng.
    public bool Ready { get; set; }

    // Tin nhắn người dùng vừa gửi NẰM NGOÀI PHẠM VI buổi làm việc: không chở điều gì về dự án hay về cách
    // dùng màn hình này (giá vàng, thời tiết, nhờ viết code…). Model tự khai — chỉ model đọc được nghĩa —
    // còn TOÀN BỘ lượt do cơ chế dựng (OffTopicExchange): câu từ chối cố định, chép lại câu hỏi đang dở, và
    // cổng tạo tài liệu đứng nguyên như thể cặp hỏi–đáp này không có. Message của model bị bỏ (prompt bắt để
    // rỗng, như lượt `ready`), và Normalize xoá mọi chỗ trả lời model lỡ kèm (chip, thẻ hỏi, ready).
    public bool OffTopic { get; set; }

    // KHÔNG có "sơ đồ luồng ở lượt mời" (FlowDiagram) nữa: luồng nghiệp vụ được chốt bằng BẢNG LUỒNG
    // (FlowMap dưới đây) — bảng sửa được từng bước, chở cả ngoại lệ, và là đường DUY NHẤT đưa luồng người
    // dùng gật tới AI Design Spec và POC. Sơ đồ cũ chỉ vẽ lại MỘT luồng chính để nhìn, ngay trước nút tạo tài
    // liệu, mà bấm "chưa đúng?" ở đó cũng không sửa được bảng đã chốt (FlowMapGate không mở lại) — xem
    // docs/requirement-flow.md.

    // BẢNG CHỐT DUY NHẤT của lượt chat — CHỈ được điền ở đúng lượt mà cổng tất định của nó
    // (InterviewTableGate) yêu cầu; mọi lượt khác để rỗng. Bản thiết kế UX và bảng phân quyền KHÔNG có ở đây:
    // BA soạn chúng ngầm bằng lời gọi riêng (UxDesignService, PermissionMatrixDraftService), không qua lượt
    // chat. Bảng thông báo cũng không: nhóm «Thông báo / nhắc nhở» không chốt ở buổi phỏng vấn —
    // xem CoverageUnaskedGroupGuard.
    //
    //  • FlowMap — các luồng nghiệp vụ theo vai trò (luồng chính + 1–2 ngoại lệ), mỗi luồng là chuỗi bước.
    public List<FlowMapRow> FlowMap { get; set; } = new();
}
