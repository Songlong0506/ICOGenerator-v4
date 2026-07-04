using System.Text.Json;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kết xuất MỘT lượt hội thoại thành text cho các ngữ cảnh gửi LLM (transcript readiness/soạn Product
/// Brief, distill bản đồ bao phủ). Điểm mấu chốt & lý do tồn tại: đính kèm các đáp án gợi ý
/// (<see cref="AgentConversation.Suggestions"/>) của lượt BA ngay sau câu hỏi. Không có chúng, một câu trả
/// lời THAM CHIẾU như "Cả hai mục tiêu trên" / "Tất cả các mục trên" trỏ tới những lựa chọn mà reader chưa
/// từng thấy → mất ngữ cảnh (bản đồ bao phủ không ghi được thông tin, readiness/Product Brief hiểu sai).
/// <para>
/// Gom về MỘT chỗ để mọi nơi đọc hội thoại đều nhất quán: trước đây <see cref="ConversationTranscriptBuilder"/>
/// và <see cref="RequirementCoverageService"/> mỗi nơi tự dựng text và cùng bỏ sót suggestions; tách riêng
/// khiến reader thứ N dễ tái lập lại đúng lỗi này. Suggestions chỉ gắn với lượt BA (lượt user không có).
/// </para>
/// </summary>
public static class ConversationTurnRenderer
{
    public static bool IsAssistant(AgentConversation turn) => turn.Role == "assistant";

    /// <summary>
    /// Nhãn vai + nội dung lượt, và với lượt BA có gợi ý thì kèm danh sách lựa chọn đã đưa ra (đánh số để
    /// câu trả lời tham chiếu "Cả hai"/"Tất cả" nối được về đúng option). KHÔNG kèm bullet/prefix của caller.
    /// </summary>
    public static string Render(AgentConversation turn)
    {
        var isAssistant = IsAssistant(turn);
        var label = isAssistant ? "BA" : "Người dùng";
        var message = (turn.Message ?? string.Empty).Trim();

        if (!isAssistant)
            return $"{label}: {message}";

        var suggestions = ParseSuggestions(turn.Suggestions);
        if (suggestions.Count == 0)
            return $"{label}: {message}";

        var options = string.Join("; ", suggestions.Select((s, i) => $"[{i + 1}] {s}"));
        return $"{label}: {message}\n   (Các lựa chọn gợi ý đã đưa cho người dùng: {options})";
    }

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.Suggestions"/> (JSON array chuỗi) an toàn: null/rỗng/hỏng
    /// đều trả mảng rỗng. Dùng chung cho cả đường render transcript lẫn <c>BuildAssistantContext</c> (dựng
    /// lại lượt BA cũ đúng JSON để củng cố format) trong <see cref="BARequirementService"/>.
    /// </summary>
    public static List<string> ParseSuggestions(string? suggestionsJson)
    {
        if (string.IsNullOrWhiteSpace(suggestionsJson))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(suggestionsJson) ?? new List<string>();
        }
        catch
        {
            // Dữ liệu cũ/không hợp lệ: bỏ qua, coi như không có gợi ý.
            return new List<string>();
        }
    }
}
