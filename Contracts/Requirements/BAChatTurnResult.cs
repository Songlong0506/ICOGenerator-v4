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

    /// <summary>True khi lời trả lời chốt là lời mời bấm "Write Requirement" — UI chuyển nút sang trạng thái sẵn sàng.</summary>
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

    /// <summary>
    /// Cổng readiness TẤT ĐỊNH (<see cref="ICOGenerator.Services.Requirements.RequirementReadinessGate"/>)
    /// xét trên <see cref="Coverage"/>: đã đủ vốn để soạn tài liệu chưa. Khác
    /// <see cref="InvitesWriteRequirement"/> ở chỗ nó KHÔNG phụ thuộc lượt vừa rồi có phải lời mời hay
    /// không — cần đúng cho một ca: bản Brief đã tồn tại, người dùng nhắn một lời đính chính, BA đáp lại
    /// bằng một câu hỏi thay vì lời mời ⇒ cổng đóng và không còn đường nào soạn lại bản Brief đã cũ.
    /// Client chỉ dùng cờ này KHI ĐÃ CÓ bản draft (data-draft-exists ở #writeReqZone); luật readiness
    /// vẫn chỉ sống ở server nên UI không có "giám khảo" thứ hai.
    /// </summary>
    public bool CoverageReady { get; set; }

    /// <summary>"Điều đã chốt" — các quyết định người dùng đã xác nhận, cập nhật tới hết lượt này.</summary>
    public List<string> Decisions { get; set; } = new();

    /// <summary>Sơ đồ luồng nghiệp vụ để user xác nhận trực quan — CHỈ có ở lượt mời "Write Requirement", rỗng ở lượt hỏi.</summary>
    public List<FlowStep> FlowDiagram { get; set; } = new();

    /// <summary>
    /// Bảng phân quyền để user chọn từng ô — CHỈ có ở lượt chốt nhóm «Phân quyền theo nghiệp vụ» (khi
    /// <see cref="ICOGenerator.Services.Requirements.PermissionMatrixGate"/> mở), rỗng ở mọi lượt khác.
    /// Đi kèm frame done để client dựng bảng tại chỗ; bản lưu trong <c>AgentConversation.PermissionMatrix</c>
    /// là thứ sống sót qua F5.
    /// </summary>
    public List<PermissionMatrixRow> PermissionMatrix { get; set; } = new();

    /// <summary>
    /// Bảng LUỒNG nghiệp vụ để user rà từng bước — CHỈ có ở lượt <see cref="ICOGenerator.Services.Requirements.FlowMapGate"/>
    /// mở. Như <see cref="PermissionMatrix"/>: đi kèm frame done để client dựng tại chỗ, còn bản sống sót
    /// qua F5 nằm ở <c>AgentConversation.FlowMap</c>.
    /// </summary>
    public List<FlowMapRow> FlowMap { get; set; } = new();

    /// <summary>Bảng MÀN HÌNH để user rà phạm vi — CHỈ có ở lượt <see cref="ICOGenerator.Services.Requirements.ScreenScopeGate"/> mở.</summary>
    public List<ScreenScopeRow> ScreenScopeMap { get; set; } = new();

    /// <summary>Bảng ĐỐI TƯỢNG nghiệp vụ — CHỈ có ở lượt <see cref="ICOGenerator.Services.Requirements.EntityMapGate"/> mở.</summary>
    public List<EntityMapRow> EntityMap { get; set; } = new();

    /// <summary>Bảng THÔNG BÁO / NHẮC NHỞ — CHỈ có ở lượt <see cref="ICOGenerator.Services.Requirements.NotificationMapGate"/> mở (bảng cuối cùng của buổi phỏng vấn).</summary>
    public List<NotificationMapRow> NotificationMap { get; set; } = new();

    /// <summary>
    /// DANH SÁCH NGƯỜI NHẬN của dự án ở lượt bày bảng thông báo — bộ đã lưu, hoặc bản gieo từ bốn mục quan
    /// hệ + các vai trò của bảng phân quyền đã chốt
    /// (<see cref="ICOGenerator.Services.Requirements.NotificationMapBuilder.RecipientOptions"/>). Đi kèm
    /// frame done vì client dựng cả bảng "Danh sách người nhận" lẫn hai ô To/CC từ đúng bộ này; rỗng ở mọi
    /// lượt khác.
    /// </summary>
    public List<string> RecipientOptions { get; set; } = new();

    /// <summary>
    /// Các BƯỚC LUỒNG đã chốt mà không màn hình nào trong bảng màn hình nhận phụ trách — phép kiểm tất
    /// định chạy bằng code (<see cref="ICOGenerator.Services.Requirements.ScreenScopeMapBuilder.UncoveredActions"/>).
    /// Chỉ có nghĩa ở lượt bày bảng màn hình; UI hiện thành một dòng nhắc dưới bảng.
    /// </summary>
    public List<string> UncoveredFlowSteps { get; set; } = new();

    /// <summary>
    /// True khi lượt chắt lọc "Bản đồ bao phủ" của lượt này THẤT BẠI (đã thử lại): <see cref="Coverage"/>
    /// là bản CŨ, chưa gộp câu trả lời vừa rồi. Phải hiện cho người dùng thấy vì triệu chứng của nó —
    /// tiến độ đứng im và BA hỏi lại nhóm vừa được trả lời — trông hệt như "BA không nghe mình nói".
    /// </summary>
    public bool CoverageStale { get; set; }
}
