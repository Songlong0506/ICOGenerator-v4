using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Phân loại một lượt NGƯỜI DÙNG theo NGUỒN GỐC của chữ trong lượt: người dùng tự gõ, hay chỉ bấm/gửi thứ
/// BA soạn sẵn (chip gợi ý, thẻ hỏi gộp, bảng chốt). Trong cột <see cref="AgentConversation.Message"/> hai
/// loại này trông y hệt nhau — một cú bấm chip "Đúng như vậy" được lưu đúng như một câu người dùng gõ.
/// <para>
/// Lý do tồn tại: <see cref="UserMemoryService"/> chắt lọc ĐẶC ĐIỂM của người dùng từ hội thoại. Không phân
/// biệt được nguồn gốc thì nó ghi chính khuôn giao diện vào hồ sơ như thể đó là tính cách: "thường xác nhận
/// bằng câu ngắn «Đúng như vậy»" (chữ trên chip BA đặt), "thích rà theo bảng rồi bấm nút gửi" (bảng chốt
/// mọi dự án đều có). Hồ sơ đó theo người dùng sang mọi dự án sau.
/// </para>
/// <para>
/// Suy theo dữ liệu ĐÃ LƯU của lượt BA ngay trước (cột gợi ý / câu hỏi gộp / bảng) chứ không thêm cột mới:
/// UI gửi chip, thẻ gộp và bảng thành tin nhắn chat thường, khuôn chữ của từng đường do chính app sinh ra
/// (requirements.js, <c>*Builder.RenderUserMessage</c>). Không khớp khuôn nào ⇒ coi là tự gõ — phía an toàn,
/// vì nhãn "tự gõ" chỉ giữ hành vi như trước khi có lớp này.
/// </para>
/// </summary>
public static class UserTurnOrigin
{
    public enum Kind
    {
        /// <summary>Người dùng tự gõ (hoặc không nhận ra khuôn nào).</summary>
        Typed,
        /// <summary>Bấm một (hoặc nhiều, ở chế độ chọn nhiều) chip gợi ý của lượt BA ngay trước.</summary>
        PickedSuggestion,
        /// <summary>Gửi thẻ hỏi gộp: mỗi dòng "- câu hỏi: đáp án", đáp án phần lớn là chip.</summary>
        AnsweredBatchCard,
        /// <summary>Gửi lại một bảng chốt BA bày sẵn (luồng, màn hình, báo cáo, phân quyền, thông báo).</summary>
        SubmittedTable
    }

    // Câu mở đầu chung của mọi *Builder.RenderUserMessage — tin nhắn hệ thống thay người dùng gửi bảng.
    private const string TableSubmissionPrefix = "Mình đã rà bảng";

    // Dấu nối các chip ở chế độ chọn nhiều (requirements.js: values.join(", ")).
    private const string MultiSelectSeparator = ", ";

    public static Kind Classify(AgentConversation userTurn, AgentConversation? previousBaTurn)
    {
        if (ConversationTurnRenderer.IsAssistant(userTurn)
            || previousBaTurn == null
            || !ConversationTurnRenderer.IsAssistant(previousBaTurn))
            return Kind.Typed;

        var message = (userTurn.Message ?? string.Empty).Trim();
        if (message.Length == 0)
            return Kind.Typed;

        if (message.StartsWith(TableSubmissionPrefix, StringComparison.Ordinal) && HasTable(previousBaTurn))
            return Kind.SubmittedTable;

        if (ConversationTurnRenderer.ParseQuestions(previousBaTurn.Questions).Count > 0
            && message.StartsWith("- ", StringComparison.Ordinal))
            return Kind.AnsweredBatchCard;

        var chips = ConversationTurnRenderer.ParseSuggestions(previousBaTurn.Suggestions)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (chips.Count > 0 && IsMadeOfChips(message, chips, previousBaTurn.SuggestionsMultiSelect))
            return Kind.PickedSuggestion;

        return Kind.Typed;
    }

    /// <summary>Nhãn vai kèm nguồn gốc, dùng thay cho "Người dùng" khi dựng ngữ cảnh chắt lọc hồ sơ.</summary>
    public static string Label(Kind kind) => kind switch
    {
        Kind.PickedSuggestion => "Người dùng [BẤM CHIP BA soạn sẵn — không phải lời tự nói]",
        Kind.AnsweredBatchCard => "Người dùng [GỬI THẺ HỎI GỘP BA soạn sẵn — đáp án trùng chip là bấm sẵn, chỉ phần sau « — » hoặc đáp án không trùng chip là tự gõ]",
        Kind.SubmittedTable => "Người dùng [GỬI LẠI BẢNG BA bày sẵn — khuôn bảng và cách rà do giao diện quy định]",
        _ => "Người dùng [TỰ GÕ]"
    };

    private static bool HasTable(AgentConversation baTurn) =>
        !string.IsNullOrWhiteSpace(baTurn.PermissionMatrix)
        || !string.IsNullOrWhiteSpace(baTurn.FlowMap)
        || !string.IsNullOrWhiteSpace(baTurn.ScreenScopeMap)
        || !string.IsNullOrWhiteSpace(baTurn.ReportMap)
        || !string.IsNullOrWhiteSpace(baTurn.NotificationMap);

    // Tin nhắn có ghép trọn từ các chip không. Chọn một: trùng đúng một chip. Chọn nhiều: tách theo ", "
    // nhưng một chip có thể tự chứa ", " — nên ăn dần từ đầu, thử chip dài nhất trước.
    private static bool IsMadeOfChips(string message, List<string> chips, bool multiSelect)
    {
        if (chips.Contains(message, StringComparer.Ordinal))
            return true;
        if (!multiSelect)
            return false;

        var ordered = chips.OrderByDescending(c => c.Length).ToList();
        var rest = message;
        while (rest.Length > 0)
        {
            var chip = ordered.FirstOrDefault(c =>
                rest == c || rest.StartsWith(c + MultiSelectSeparator, StringComparison.Ordinal));
            if (chip == null)
                return false;
            rest = rest == chip ? string.Empty : rest[(chip.Length + MultiSelectSeparator.Length)..];
        }
        return true;
    }
}
