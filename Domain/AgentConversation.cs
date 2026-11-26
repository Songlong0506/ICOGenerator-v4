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

    // JSON array (chuỗi) BẢNG LUỒNG (FlowMapRow[] — luồng nghiệp vụ theo vai trò) ở đúng lượt BA bày nó
    // ra; null với mọi lượt khác. Đây là bảng chốt DUY NHẤT còn được bày trong chat: bảng phân quyền và
    // bảng báo cáo do BA soạn ngầm vào Project.PermissionMatrix / Project.ReportMap, phạm vi màn hình là
    // bản thiết kế UX ở Project.UxBlueprint, nhóm «Thông báo / nhắc nhở» không chốt ở buổi phỏng vấn (xem
    // CoverageUnaskedGroupGuard).
    // Lưu lại vì đúng hai lý do của cột ColumnMap: F5 giữa chừng mà bảng biến mất thì người dùng mất luôn
    // các dòng chưa sửa, và Message của lượt đó chỉ là câu dẫn — không có cột này thì mọi reader
    // transcript không biết BA đã đề xuất gì để người dùng gật/lắc.
    // Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? FlowMap { get; set; }

    // JSON array (chuỗi) các file người dùng đính kèm ở lượt user này (ChatAttachment[]: id + tên +
    // cờ ảnh, trỏ về ProjectSourceFile) — để bubble hiển thị ảnh ngay trong hội thoại như ChatGPT/Claude.
    // File gốc vẫn sống ở "Tài liệu nguồn"; xóa nguồn thì bubble chỉ mất ảnh xem trước (id hỏng → ẩn).
    // Null với lượt không đính kèm. Là nội dung yêu cầu nên mã hóa at rest như Message/Suggestions.
    public string? Attachments { get; set; }

    // CỔNG READINESS ĐÃ PASS TẠI ĐÚNG LƯỢT NÀY — cờ do hệ thống đặt, không phải thứ suy lại từ nội dung
    // lượt chat. true ⇔ tại thời điểm lưu lượt này, cổng tất định (RequirementReadinessGate.Evaluate) đã
    // xét trên bản đồ bao phủ hiện hành và cho qua. Bước soạn tài liệu đọc cờ của lượt ĐANG ĐỨNG CUỐI để
    // biết mình có được bỏ qua lần xét lại hay không (xem ProductBriefDraftService).
    //
    // Vì sao là một cột chứ không phải phép dò chuỗi: trước đây bước soạn tài liệu tự suy bằng cách tìm
    // cụm "Write Requirement" trong lượt cuối. Cách đó hỏng ở hai chỗ — (1) nó phụ thuộc vào CHỮ mà model
    // sinh ra, nên một lần chỉnh prompt/đổi cách diễn đạt là mất tín hiệu trong im lặng; (2) mọi đường ghi
    // thêm một lượt SAU lời mời đều vô tình xoá tín hiệu, kể cả đường không hề mang thông tin mới — một
    // cặp lượt ghi xen vào ngay trước lúc soạn cũng đủ đá vòng soạn về khung chat và bắt người dùng bấm
    // nút lần hai.
    //
    // FAIL-CLOSED: mặc định false. Lượt của bất kỳ đường ghi nào không tự khẳng định cờ này đều làm bước
    // soạn tài liệu xét lại cổng — thừa một lượt distill thì chỉ tốn token, còn bỏ qua cổng nhầm thì tài
    // liệu được viết trên một bản đồ còn lỗ. Các lượt CŨ (ghi trước khi có cột) mang false và tự lành ở
    // lượt mời kế tiếp.
    public bool ReadinessVerified { get; set; }

    // Lượt BA này CÓ PHẢI lời xin tài liệu nguồn không (SourceRequestTurn). Cột chứ không phải phép dò
    // chữ, vì câu hỏi "BA đã xin file lần nào chưa" được hỏi lại ở MỌI lượt sau: trước đây nó được trả
    // lời bằng cách quét cụm "📎"/"đính kèm"/"gửi giúp" trên toàn bộ lượt BA cũ — tức app đoán lại chính
    // chuỗi SourceRequestTurn.Message mà nó tự phát ra. Hai chiều hỏng đều thật: một lượt BA vô tình có
    // chữ "đính kèm" là khoá vĩnh viễn đường xin file, còn sửa câu Message là mất dấu mọi lượt đã xin.
    // Cờ được đặt MỘT LẦN lúc ghi lượt (xem BAChatService.ApplySourceRequestTurn) và mọi đường đọc sau
    // chỉ đọc cột.
    //
    // FAIL-OPEN: mặc định false ⇒ lượt cũ không có cờ chỉ khiến BA có thể xin file thêm một lần, không
    // chặn gì.
    //
    // Cờ nói lượt ĐÓ có nhờ gửi file, không nói nó xin đích danh một file nào: lời mời chung chung ở lượt mở
    // đầu ("có file Excel hay biểu mẫu gì thì đính kèm nhé") cũng mang cờ. Suất "chỉ xin MỘT lần" của lượt
    // xin file tất định vì thế không đếm cờ này một mình — nó đếm cặp (lượt user MentionsSource → lượt BA
    // SourceRequested), xem SourceRequestTurn.AnsweredMention.
    public bool SourceRequested { get; set; }

    // Lượt USER này có nhắc tới một nơi dữ liệu của họ ĐANG NẰM SẴN và đính kèm được (file Excel, bảng tính,
    // biểu mẫu…) không — phán quyết `userMentionsExistingSource` của lượt trọng tài (InterviewJudge), đóng
    // dấu lên lượt user lúc lưu lượt BA đáp nó (BAChatService.SaveTurnAsync, cùng chỗ với OffTopic; chạy
    // lại lượt thì đặt lại theo phán quyết mới).
    //
    // Vì sao phải lưu: suất "chỉ xin file MỘT lần" phải phân biệt LỜI XIN ĐÍCH DANH — lượt BA đáp đúng một
    // lượt nhắc nguồn — với lời mời chung chung đầu buổi. Trước đây suất đó chỉ đọc SourceRequested, mà
    // lượt mở đầu theo prompt luôn có nút 📎 nên luôn mang cờ: ca thật (dự án Learning & Development) người
    // dùng đáp "File Excel, Email" ở lượt 2, và đường xin file tất định không bao giờ bắn vì suất đã bị lượt
    // mở đầu đốt mất. Phán quyết chỉ đúng ở lượt nó được đưa ra (nó đọc đúng lượt user mới nhất), nên nó
    // phải được ghi lại ngay lúc đó.
    //
    // Chỉ có nghĩa khi trọng tài được gọi để phán điều này — tức khi đường xin file còn cửa (chưa có tài
    // liệu nguồn, chưa có lời xin đích danh). Ngoài lúc đó cờ là false, và cũng không ai cần đọc nó.
    // Mặc định false: lượt cũ coi như không nhắc ⇒ cùng lắm BA xin file thêm một lần.
    public bool MentionsSource { get; set; }

    // Lượt thuộc một CẶP HỎI–ĐÁP NGOÀI PHẠM VI: người dùng hỏi điều không dính gì tới dự án (giá vàng,
    // thời tiết, nhờ viết code…) và BA từ chối ngắn. Đặt trên CẢ HAI lượt của cặp — lượt user lẫn lượt BA
    // đáp nó — vì mọi câu hỏi phía sau đều hỏi về cả cặp: "từ lần soạn Brief gần nhất người dùng đã nói
    // thêm gì về dự án chưa", "lượt BA đang đứng cuối là lời mời hay câu hỏi".
    //
    // Cặp này TRONG SUỐT với mọi cơ chế phía yêu cầu (OffTopicExchange): cổng tạo tài liệu, bước soạn
    // Brief, các bản đúc kết đều đọc hội thoại như thể cặp đó không có. Ca thật sinh ra cột này: Brief V1
    // đã duyệt, POC đã dựng, người dùng hỏi "giá vàng hôm nay là bao nhiêu" — BA không đáp câu nào mà nút
    // "Write Requirement" sáng lại, vì một lượt user bất kỳ sau lần soạn là đủ để cổng rời trạng thái
    // "đã soạn, chưa có gì mới".
    //
    // Cờ do MODEL khai (BAChatReply.OffTopic) và được đóng dấu một lần lúc lưu lượt BA; lượt user nhận
    // cùng giá trị ngay lúc đó (BAChatService.SaveTurnAsync). Chạy lại lượt (sửa / soạn lại) thì cờ của
    // lượt user được đặt lại theo câu trả lời mới. Mặc định false: lượt cũ là lượt thường.
    public bool OffTopic { get; set; }

    // Thời điểm lượt bị LƯU TRỮ bởi "New Chat" (null = đang thuộc hội thoại hiện hành). Hội thoại là
    // nguồn gốc pháp lý của mọi tài liệu sinh ra sau đó nên không bao giờ xóa cứng: New Chat chỉ đóng
    // dấu ArchivedAt, mọi đường đọc (UI, memory, transcript) lọc ArchivedAt == null.
    public DateTime? ArchivedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
