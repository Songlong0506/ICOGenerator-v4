using System.Text;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng bản ghi hội thoại Hỏi–Đáp (BA hỏi / Người dùng trả lời) từ các lượt chat, dùng làm đầu vào cho
/// cổng readiness và bước soạn Product Brief. Trước đây hai bước này chỉ nhận các lượt CỦA USER — mất
/// sạch câu hỏi của BA, nên câu trả lời ngắn kiểu chip gợi ý ("Nhân viên văn phòng", "Có, cần duyệt")
/// trở nên vô nghĩa vì không biết đang trả lời cho câu hỏi nào. Giữ cả hai vai để mỗi câu trả lời còn
/// nguyên ngữ cảnh.
/// </summary>
public static class ConversationTranscriptBuilder
{
    // Lượt "BA" là thông báo lỗi gọi AI (được surface vào khung chat thay vì ném 500) — không phải nội
    // dung yêu cầu, đưa vào transcript chỉ gây nhiễu nên lọc bỏ. Khớp tiền tố ghi ở BARequirementService.
    public const string LlmFailurePrefix = "⚠️ Lời gọi AI thất bại";

    public static string Build(IEnumerable<AgentConversation> conversations)
    {
        // Thứ tự ổn định (CreatedAt rồi Id) như các chỗ đọc hội thoại khác — CreatedAt có thể trùng
        // tới mili-giây giữa hai lượt liền nhau.
        var turns = conversations
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        var sb = new StringBuilder();
        var hasUserTurn = false;
        foreach (var turn in turns)
        {
            var message = (turn.Message ?? string.Empty).Trim();
            if (message.Length == 0)
                continue;

            var isAssistant = turn.Role == "assistant";
            if (isAssistant && message.StartsWith(LlmFailurePrefix, StringComparison.Ordinal))
                continue;

            hasUserTurn |= !isAssistant;
            sb.Append(isAssistant ? "BA: " : "Người dùng: ").AppendLine(message);
        }

        // Chưa có lượt user nào (mới chỉ BA chào/hỏi) ⇒ chưa có yêu cầu để tổng hợp.
        return hasUserTurn ? sb.ToString().TrimEnd() : "(Chưa có yêu cầu nào được ghi nhận.)";
    }
}
