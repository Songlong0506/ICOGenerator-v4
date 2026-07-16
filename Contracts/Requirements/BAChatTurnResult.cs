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
}
