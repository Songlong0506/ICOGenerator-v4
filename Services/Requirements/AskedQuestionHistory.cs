using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sổ "những câu BA ĐÃ HỎI" của một hội thoại — và phép thử TẤT ĐỊNH xem một câu hỏi mới có phải là
/// câu cũ hỏi lại hay không.
///
/// <para>
/// Lý do tồn tại: trước lớp này, thứ DUY NHẤT ngăn BA hỏi lại là bản đồ bao phủ — mà bản đồ chỉ có độ
/// phân giải theo NHÓM (12 dòng), không theo câu hỏi. Một dòng chưa đạt chuẩn <c>[RÕ]</c> (chuẩn cố ý
/// khắt khe trong <c>requirement-coverage.v3.md</c>) đồng nghĩa "ưu tiên hỏi nhóm này", và vì mỗi câu
/// hỏi trong lượt gộp được yêu cầu gắn <c>group</c> = tên dòng bản đồ, model sinh lại ĐÚNG câu hỏi mở
/// cũ của chính nhóm đó. Người dùng vừa trả lời xong đã bị hỏi lại nguyên văn — kèm chip gợi ý chính là
/// câu trả lời họ vừa gõ. Cùng chuyện đó xảy ra khi lượt chắt lọc bản đồ lỗi (fail-open giữ bản cũ):
/// bản đồ không nhúc nhích thì cả cụm câu hỏi của lượt trước được phát lại y nguyên.
/// </para>
/// <para>
/// Prompt đã cấm hỏi lại, nhưng prompt chỉ định hướng; lớp này mới là cái phanh. Nó vừa dựng phần
/// "Các câu hỏi đã hỏi" nạp vào ngữ cảnh (<see cref="BuildNote"/>), vừa lọc thẳng các câu trùng ra khỏi
/// lượt trả lời trước khi lưu (<see cref="IsRepeat"/>).
/// </para>
/// </summary>
public static class AskedQuestionHistory
{
    /// <summary>Số câu hỏi cũ tối đa nạp vào ngữ cảnh (lấy các câu GẦN NHẤT) để không phình prompt.</summary>
    public const int MaxQuestionsInNote = 24;

    // Câu quá ngắn ("Đúng không ạ?", "Còn gì nữa không?") chỉ so khớp TUYỆT ĐỐI: chúng vốn hay lặp lại
    // một cách hợp lệ, đo tương đồng mờ trên vài từ thì cái gì cũng giống cái gì.
    private const int MinLengthForFuzzyMatch = 24;

    // Hai thước đo cùng phải đạt thì mới coi là trùng:
    //  - BAO PHỦ (shared / số từ của câu NGẮN hơn): bắt câu cũ được viết lại gọn đi vài chữ — ca thật đã
    //    gặp là "Ai sẽ sử dụng app này và vai trò của họ?" quay lại thành "Ai sẽ dùng app và vai trò của
    //    họ?" (bao phủ 8/9). Một mình nó thì quá tay: câu ngắn nào nằm lọt trong câu dài cũng thành trùng.
    //  - JACCARD (shared / hợp): chặn đúng cái quá tay đó — hai câu phải cùng cỡ mới qua được.
    // Một câu hỏi đào sâu thật sự ("trong hai phòng anh/chị vừa kể, ai gọi điện nhắc?") rơi xa cả hai
    // ngưỡng, nên nó không bị chặn oan — đó mới là việc BA phải làm với một nhóm [MỘT PHẦN].
    private const double RepeatContainment = 0.8;
    private const double RepeatJaccard = 0.5;

    /// <summary>
    /// Mọi câu hỏi BA đã đặt trong các lượt đang xét, theo đúng thứ tự hỏi. Gồm cả câu của lượt hỏi GỘP
    /// (cột <see cref="AgentConversation.Questions"/>) lẫn lượt hỏi MỘT câu (khi đó <c>Message</c> chính
    /// là câu hỏi — nhận diện qua việc lượt đó có gợi ý, đúng như prompt bắt buộc mọi câu hỏi phải kèm
    /// gợi ý). Lượt ⚠️ báo lỗi gọi AI bị bỏ: nó không phải câu hỏi.
    /// </summary>
    public static List<string> Collect(IEnumerable<AgentConversation> turns)
    {
        var asked = new List<string>();
        foreach (var turn in turns)
        {
            if (!ConversationTurnRenderer.IsAssistant(turn))
                continue;

            var message = (turn.Message ?? string.Empty).Trim();
            if (message.StartsWith(ConversationTranscriptBuilder.LlmFailurePrefix, StringComparison.Ordinal))
                continue;

            var questions = ConversationTurnRenderer.ParseQuestions(turn.Questions);
            if (questions.Count > 0)
            {
                asked.AddRange(questions.Select(q => q.Question.Trim()).Where(q => q.Length > 0));
                continue;
            }

            // Lượt hỏi một câu: Message CHỞ câu hỏi. Không có gợi ý ⇒ lượt tóm tắt/thông báo, không phải
            // câu hỏi — bỏ qua để không chặn oan các lượt xác nhận về sau.
            if (message.Length > 0 && ConversationTurnRenderer.ParseSuggestions(turn.Suggestions).Count > 0)
                asked.Add(message);
        }

        return asked;
    }

    /// <summary>Khoá so khớp: bỏ dấu câu/ký tự trang trí, gộp khoảng trắng, hạ chữ thường.</summary>
    public static string Key(string? text)
    {
        var sb = new StringBuilder();
        var lastWasSpace = true;
        foreach (var ch in (text ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Khoá của mọi câu đã hỏi — truyền vào <see cref="IsRepeat"/> để không chuẩn hoá lặp lại.</summary>
    public static HashSet<string> Keys(IEnumerable<string> asked)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in asked)
        {
            var key = Key(question);
            if (key.Length > 0)
                keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// <paramref name="candidate"/> có phải câu đã hỏi rồi không: trùng khoá tuyệt đối, hoặc (với câu đủ
    /// dài) tập từ trùng nhau từ <see cref="RepeatSimilarity"/> trở lên — bắt được cả câu cũ sửa vài chữ.
    /// </summary>
    public static bool IsRepeat(string? candidate, IReadOnlyCollection<string> askedKeys)
    {
        var key = Key(candidate);
        if (key.Length == 0 || askedKeys.Count == 0)
            return false;

        if (askedKeys.Contains(key))
            return true;

        if (key.Length < MinLengthForFuzzyMatch)
            return false;

        var tokens = new HashSet<string>(key.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (tokens.Count == 0)
            return false;

        foreach (var asked in askedKeys)
        {
            if (asked.Length < MinLengthForFuzzyMatch)
                continue;

            var other = new HashSet<string>(asked.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            if (other.Count == 0)
                continue;

            var shared = tokens.Count(other.Contains);
            var union = tokens.Count + other.Count - shared;
            var containment = (double)shared / Math.Min(tokens.Count, other.Count);
            if (union > 0 && containment >= RepeatContainment && (double)shared / union >= RepeatJaccard)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Các nhãn nhóm ĐƯỢC PHÉP hỏi lại: nhóm người dùng vừa bấm "chưa đúng?" trên panel tiến độ
    /// (<see cref="CoverageMapEditor.Reopen"/> đánh dấu bằng <see cref="CoverageMapEditor.ReopenNote"/>).
    /// Không có ngoại lệ này thì phanh chống-hỏi-lại sẽ chặn đúng cái đường mà người dùng vừa chủ động
    /// mở ra — họ bảo "nhóm này BA hiểu sai, hỏi lại giúp tôi" mà BA không được phép hỏi lại.
    /// </summary>
    public static HashSet<string> ReopenedGroups(IEnumerable<CoverageMapItem> coverage)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in coverage)
        {
            if (item.Summary.Contains(CoverageMapEditor.ReopenNote, StringComparison.OrdinalIgnoreCase))
                groups.Add(Key(item.Label));
        }
        return groups;
    }

    /// <summary>True khi câu hỏi này nhắm vào một nhóm người dùng vừa mở lại ⇒ miễn phép thử trùng lặp.</summary>
    public static bool IsExempt(BAChatQuestion question, IReadOnlyCollection<string> reopenedGroups) =>
        reopenedGroups.Count > 0 && reopenedGroups.Contains(Key(question.Group));

    /// <summary>
    /// Khối system message liệt kê các câu đã hỏi. Chuỗi rỗng khi chưa hỏi câu nào (không nạp khối trống
    /// vào prompt). Lấy <see cref="MaxQuestionsInNote"/> câu GẦN NHẤT — câu cũ hơn thế đã nằm trong bộ
    /// nhớ tóm tắt và cũng ít khả năng bị phát lại.
    /// </summary>
    public static string BuildNote(IReadOnlyList<string> asked)
    {
        if (asked.Count == 0)
            return string.Empty;

        var recent = asked.Count > MaxQuestionsInNote
            ? asked.Skip(asked.Count - MaxQuestionsInNote).ToList()
            : asked;

        var sb = new StringBuilder();
        sb.AppendLine("## Các câu hỏi BẠN ĐÃ HỎI ở những lượt trước (TUYỆT ĐỐI KHÔNG phát lại)");
        sb.AppendLine("Người dùng đã trả lời (hoặc đã chủ động bỏ qua) những câu này. Hỏi lại là bắt họ gõ lại điều vừa nói.");
        sb.AppendLine("Nhóm của câu nào còn chưa `[RÕ]` thì hỏi ĐÚNG phần `còn thiếu:` mà bản đồ bao phủ ghi, bằng một câu hỏi KHÁC hẳn — đừng phát lại câu mở đầu của nhóm đó.");
        sb.AppendLine("Hệ thống đối chiếu MÁY MÓC: câu hỏi trùng với danh sách dưới đây sẽ bị loại khỏi lượt trả lời của bạn.");
        foreach (var question in recent)
            sb.AppendLine($"- {question}");

        return sb.ToString().TrimEnd();
    }
}
