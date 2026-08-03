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
    /// Các câu hỏi của một lượt hỏi GỘP (2–4 câu độc lập) — UI dựng thẻ nhiều dòng, người dùng trả lời
    /// cả cụm rồi gửi trong MỘT lượt. Rỗng ở lượt hỏi một câu (khi đó <see cref="Suggestions"/> chở gợi ý).
    /// Hai danh sách này loại trừ nhau: BAChatReplyParser.Normalize đảm bảo không bao giờ có cả hai.
    /// </summary>
    public List<BAChatQuestion> Questions { get; set; } = new();

    /// <summary>Bản đồ bao phủ yêu cầu đã parse (rỗng khi chưa có) — UI cập nhật panel tiến độ không cần reload.</summary>
    public List<CoverageMapItem> Coverage { get; set; } = new();

    /// <summary>"Điều đã chốt" — các quyết định người dùng đã xác nhận, cập nhật tới hết lượt này.</summary>
    public List<string> Decisions { get; set; } = new();

    /// <summary>Sơ đồ luồng nghiệp vụ để user xác nhận trực quan — CHỈ có ở lượt mời "Write Requirement", rỗng ở lượt hỏi.</summary>
    public List<FlowStep> FlowDiagram { get; set; } = new();

    /// <summary>
    /// True khi lượt chắt lọc "Bản đồ bao phủ" của lượt này THẤT BẠI (đã thử lại): <see cref="Coverage"/>
    /// là bản CŨ, chưa gộp câu trả lời vừa rồi. Phải hiện cho người dùng thấy vì triệu chứng của nó —
    /// tiến độ đứng im và BA hỏi lại nhóm vừa được trả lời — trông hệt như "BA không nghe mình nói".
    /// </summary>
    public bool CoverageStale { get; set; }
}
