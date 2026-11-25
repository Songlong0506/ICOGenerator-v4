namespace ICOGenerator.Contracts.Requirements;

// Kết quả ĐẦY ĐỦ của một lượt chat BA — mở rộng của ChatWithBAResult (enum trạng thái) để endpoint
// streaming (Requirements/ChatStream) trả được bản chốt cho client render tại chỗ mà không cần
// reload trang: text trả lời cuối cùng (sau parser + cổng readiness), danh sách gợi ý, và cờ
// "BA đã mời bấm Write Requirement" để UI bật trạng thái nút. Thuần POCO nên sống ở Contracts,
// cùng chỗ với ChatWithBAResult.
public class BAChatTurnResult
{
    public ChatWithBAResult Status { get; set; } = ChatWithBAResult.Ok;

    /// <summary>Lời trả lời CHỐT của BA (đã qua parser và cổng readiness) — đúng bản được lưu vào hội thoại.</summary>
    public string Reply { get; set; } = string.Empty;

    /// <summary>Gợi ý trả lời nhanh cho lượt này (rỗng khi BA không đặt câu hỏi).</summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>
    /// True khi lời trả lời chốt là lời mời bấm "Write Requirement" — tín hiệu DUY NHẤT mở cổng tạo tài liệu
    /// ở frame done. Cờ này đã qua cả hai chốt chặn tất định của <c>ApplyReadinessGate</c> (lượt không đang
    /// hỏi + bản đồ bao phủ đủ), nên cổng mở ⇒ BA không còn câu hỏi nào đang chờ trả lời.
    ///
    /// <para>
    /// ĐỪNG THÊM một cờ "bản đồ đã đủ" song song để mở cổng mà không đọc lượt này. Cờ đó từng có
    /// (<c>CoverageReady</c>, dùng khi bản Brief đã tồn tại) và nó mở cổng đúng ở lượt BA đang HỎI: câu hỏi
    /// và nút "Write Requirement" đứng cạnh nhau, kèm câu "mình đã thu thập đủ thông tin" ngay dưới một câu
    /// hỏi chưa ai trả lời. Ca kẹt mà nó định cứu (Brief đã có, BA đáp lời đính chính bằng một câu hỏi) tự
    /// gỡ theo đường thường: người dùng trả lời ⇒ BA mời — xem docs/requirement-flow.md.
    /// </para>
    /// </summary>
    public bool InvitesWriteRequirement { get; set; }

    /// <summary>True khi câu hỏi lượt này cho phép chọn NHIỀU gợi ý cùng lúc (UI đổi chip sang chế độ toggle + nút gửi).</summary>
    public bool SuggestionsMultiSelect { get; set; }

    /// <summary>
    /// True khi lượt hỏi MỘT câu này là câu MỞ (xin lời kể/mô tả): không có chip, UI đổi gợi ý ở ô nhập
    /// thành lời mời kể tự do. Chỉ đi theo frame done của lượt vừa chạy — KHÔNG lưu xuống DB, nên sau khi
    /// tải lại trang lời mời đó không còn. Cố tình dừng ở đây: thứ thật sự phải đúng là "không có chip
    /// đánh lừa" và điều đó tự đúng khi hội thoại được render lại (lượt không có gợi ý thì không có chip);
    /// phần còn lại chỉ là một dòng nhắc, không đáng một cột CSDL mới cùng migration đi kèm.
    /// </summary>
    public bool OpenEnded { get; set; }

    /// <summary>
    /// Các câu hỏi của một lượt hỏi GỘP (2–4 câu độc lập) — UI dựng thẻ nhiều dòng, người dùng trả lời
    /// cả cụm rồi gửi trong MỘT lượt. Rỗng ở lượt hỏi một câu (khi đó <see cref="Suggestions"/> chở gợi ý).
    /// Hai danh sách này loại trừ nhau: BAChatReplyParser.Normalize đảm bảo không bao giờ có cả hai.
    /// </summary>
    public List<BAChatQuestion> Questions { get; set; } = new();

    /// <summary>Bản đồ bao phủ yêu cầu đã parse (rỗng khi chưa có) — UI cập nhật panel tiến độ không cần reload.</summary>
    public List<CoverageMapItem> Coverage { get; set; } = new();

    // KHÔNG có "Điều đã chốt" (Decisions) ở đây: nhật ký quyết định vẫn được gộp ở hậu kỳ mỗi lượt
    // (BAChatService.UpdateDecisionsAsync) nhưng không còn mặt UI nào để đẩy về — bản tổng kết ở cổng tạo
    // tài liệu đã gỡ, xem Views/Requirements/Index.cshtml. Người đọc nhật ký nay chỉ còn là máy.

    // KHÔNG có FlowDiagram ở đây: sơ đồ luồng chỉ-đọc ở lượt mời đã gỡ, luồng được chốt bằng bảng luồng
    // (FlowMap bên dưới) — xem BAChatReply.

    /// <summary>
    /// Bảng LUỒNG nghiệp vụ để user rà từng bước — CHỈ có ở lượt <see cref="ICOGenerator.Services.Requirements.FlowMapGate"/>
    /// mở. Đi kèm frame done để client dựng tại chỗ, còn bản sống sót qua F5 nằm ở
    /// <c>AgentConversation.FlowMap</c>.
    /// </summary>
    public List<FlowMapRow> FlowMap { get; set; } = new();

    /// <summary>
    /// True khi lượt chắt lọc "Bản đồ bao phủ" của lượt này THẤT BẠI (đã thử lại): <see cref="Coverage"/>
    /// là bản CŨ, chưa gộp câu trả lời vừa rồi. Phải hiện cho người dùng thấy vì triệu chứng của nó —
    /// tiến độ đứng im và BA hỏi lại nhóm vừa được trả lời — trông hệt như "BA không nghe mình nói".
    /// </summary>
    public bool CoverageStale { get; set; }
}
