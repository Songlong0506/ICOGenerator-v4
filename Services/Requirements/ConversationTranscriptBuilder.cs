using System.Text;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng bản ghi hội thoại Hỏi–Đáp (BA hỏi / Người dùng trả lời) từ các lượt chat, dùng làm đầu vào cho
/// bước soạn Product Brief. Trước đây bước này chỉ nhận các lượt CỦA USER — mất
/// sạch câu hỏi của BA, nên câu trả lời ngắn kiểu chip gợi ý ("Nhân viên văn phòng", "Có, cần duyệt")
/// trở nên vô nghĩa vì không biết đang trả lời cho câu hỏi nào. Giữ cả hai vai để mỗi câu trả lời còn
/// nguyên ngữ cảnh, và render qua <see cref="ConversationTurnRenderer"/> để lượt BA kèm luôn các đáp án
/// gợi ý — nếu không, đáp án tham chiếu như "Cả hai mục tiêu trên" trỏ tới option reader chưa từng thấy.
/// </summary>
public static class ConversationTranscriptBuilder
{
    // Lượt "BA" là thông báo lỗi gọi AI (được surface vào khung chat thay vì ném 500) — không phải nội
    // dung yêu cầu, đưa vào transcript chỉ gây nhiễu nên lọc bỏ. Khớp tiền tố ghi ở BAChatService.
    public const string LlmFailurePrefix = "⚠️ Lời gọi AI thất bại";

    public const string NoRequirementPlaceholder = "(Chưa có yêu cầu nào được ghi nhận.)";

    /// <summary>Transcript nguyên văn + số lượt cũ đã bị bỏ (0 khi gửi trọn hội thoại).</summary>
    public sealed record Transcript(string Text, int SkippedTurns);

    public static string Build(IEnumerable<AgentConversation> conversations)
        => BuildWindowed(conversations, summarizedTurnCount: 0, approvedTurnCount: 0).Text;

    /// <summary>
    /// Như <see cref="Build"/> nhưng CẮT phần đầu hội thoại theo <see cref="BriefContextWindow"/>: các
    /// lượt bị bỏ đã nằm trong <c>Project.ConversationSummary</c> (khối tóm tắt đi kèm prompt), nên
    /// không có thông tin nào bốc hơi — xem bất biến ở <see cref="BriefContextWindow"/>.
    /// </summary>
    public static Transcript BuildWindowed(IEnumerable<AgentConversation> conversations, int summarizedTurnCount, int approvedTurnCount)
    {
        // Thứ tự ổn định (CreatedAt rồi Id) như các chỗ đọc hội thoại khác — CreatedAt có thể trùng
        // tới mili-giây giữa hai lượt liền nhau. Phải TRÙNG thứ tự mà ConversationMemoryService dùng để
        // đếm con trỏ, nếu không "lượt thứ n" của hai bên trỏ vào hai dòng khác nhau.
        var turns = conversations
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        // Render TRƯỚC cho toàn bộ lượt: cửa sổ cắt theo ký tự cần độ dài thật (một lượt chốt bảng dài
        // gấp hàng chục lần một lượt hỏi đáp), và các con trỏ bộ nhớ đếm trên MỌI dòng hội thoại nên lượt
        // bị lọc vẫn phải giữ chỗ — để null cho chỗ đó, độ dài 0.
        var rendered = new List<string?>(turns.Count);
        var lengths = new List<int>(turns.Count);
        var hasUserTurn = false;

        foreach (var turn in turns)
        {
            var message = (turn.Message ?? string.Empty).Trim();
            var isAssistant = ConversationTurnRenderer.IsAssistant(turn);
            var skip = message.Length == 0
                || (isAssistant && message.StartsWith(LlmFailurePrefix, StringComparison.Ordinal));

            if (skip)
            {
                rendered.Add(null);
                lengths.Add(0);
                continue;
            }

            hasUserTurn |= !isAssistant;
            var text = ConversationTurnRenderer.Render(turn);
            rendered.Add(text);
            lengths.Add(text.Length);
        }

        // Chưa có lượt user nào (mới chỉ BA chào/hỏi) ⇒ chưa có yêu cầu để tổng hợp.
        if (!hasUserTurn)
            return new Transcript(NoRequirementPlaceholder, 0);

        var skipCount = BriefContextWindow.ComputeSkip(lengths, summarizedTurnCount, approvedTurnCount);

        var sb = new StringBuilder();
        for (var i = skipCount; i < rendered.Count; i++)
        {
            if (rendered[i] is { } line)
                sb.AppendLine(line);
        }

        // Cắt xong mà phần còn lại chỉ toàn lượt bị lọc (hội thoại rất ngắn ở đuôi) ⇒ thà gửi trọn hội
        // thoại còn hơn gửi một transcript rỗng: chốt chặn rẻ, gần như không bao giờ chạy.
        if (sb.Length == 0)
            return new Transcript(string.Join(Environment.NewLine, rendered.Where(x => x != null)), 0);

        return new Transcript(sb.ToString().TrimEnd(), skipCount);
    }
}
