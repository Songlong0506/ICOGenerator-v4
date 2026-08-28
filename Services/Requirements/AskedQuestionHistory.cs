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
    // 0.5 là ngưỡng của thời phép thử chạy trên TOÀN BỘ `Message`. Từ khi <see cref="QuestionCore"/> lược
    // phần "mình đã ghi nhận…" đi, hai vế đem so ngắn hơn hẳn và giống nhau hơn hẳn — đo lại trên chính
    // buổi JD Libary 4: hai lượt hỏi lại thật (20↔16, 20↔18) lên 0.73 và 0.71, còn một câu hỏi tiếp NỬA
    // CÒN LẠI của một câu kép (18↔16, BA hỏi vế "chỉnh sửa" sau khi người dùng chỉ trả lời vế "ngừng sử
    // dụng") ở 0.59. Giữ 0.5 là chặn oan đúng cái lượt đắt nhất của buổi đó — lượt 19 chở nguyên luật
    // upgrade version. Nâng lên 0.6 tách được hai bên; ca "câu cũ rụng vài chữ" mà phanh này sinh ra để
    // bắt vẫn ở 0.67 nên không mất.
    private const double RepeatJaccard = 0.6;

    /// <summary>
    /// Mọi câu hỏi BA đã đặt trong các lượt đang xét, theo đúng thứ tự hỏi. Gồm cả câu của lượt hỏi GỘP
    /// (cột <see cref="AgentConversation.Questions"/>) lẫn lượt hỏi MỘT câu (khi đó <c>Message</c> chính
    /// là câu hỏi — nhận diện qua việc lượt đó có gợi ý, HOẶC có dấu hỏi: câu MỞ được phép bỏ chip nên
    /// vế thứ hai là thứ duy nhất nhìn thấy nó). Lượt ⚠️ báo lỗi gọi AI, lượt tóm tắt/thông báo (không
    /// hỏi gì) và lượt bày BẢNG CHỐT (chỗ trả lời là cái bảng, `Message` chỉ là câu dẫn) đều bị bỏ.
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

            // Lượt hỏi một câu: Message CHỞ câu hỏi. Nhận diện bằng "có chip HOẶC có dấu hỏi" — đúng cặp
            // điều kiện mà BAChatService dùng ở phía ĐỐI CHIẾU. Trước đây vế đầu là đủ, vì mọi câu hỏi
            // đều bắt buộc kèm chip; từ khi CÂU MỞ được phép bỏ chip (xem BAChatQuestion.OpenEnded) thì
            // đúng loại câu đắt nhất — xin một lời KỂ — không bao giờ vào sổ, nên phanh không có gì để
            // so và câu đó được phát lại nguyên văn. Ca thật (dự án JD Libary, lượt 2 và lượt 4):
            // *"anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên: bắt đầu từ
            // đâu, làm những bước nào, và ai tham gia?"* hỏi hai lượt liền, không lệch một chữ.
            //
            // Dấu hỏi là ranh giới (cùng phép thử với chốt chặn lượt câm ở BAChatService): lượt tóm
            // tắt/thông báo không có nó nên vẫn đứng ngoài sổ, đúng như trước.
            //
            // Sổ này giữ NGUYÊN VĂN lượt đã hỏi, không cắt vế hỏi: nó còn là khối "các câu BẠN ĐÃ HỎI" nạp
            // vào ngữ cảnh (<see cref="BuildNote"/>), và ở đó model cần đọc đúng câu như nó đã lên màn hình.
            // Việc cắt là chuyện của phép SO KHỚP, làm bên trong <see cref="Keys"/>/<see cref="IsRepeat"/>.
            if (message.Length > 0
                && (ConversationTurnRenderer.ParseSuggestions(turn.Suggestions).Count > 0 || AsksSomething(message))
                && !CarriesTable(turn))
                asked.Add(message);
        }

        return asked;
    }

    /// <summary>Lượt này có HỎI gì không — dấu hỏi là ranh giới duy nhất đọc được từ một lượt đã lưu.</summary>
    private static bool AsksSomething(string message)
        => message.Contains('?', StringComparison.Ordinal) || message.Contains('\uff1f', StringComparison.Ordinal);

    /// <summary>
    /// Lượt bày một BẢNG CHỐT (bảng cột, phân quyền, luồng, màn hình, đối tượng, báo cáo, thông báo).
    /// Đứng ngoài sổ: chỗ trả lời của lượt đó là chính cái bảng, còn `Message` chỉ là câu dẫn — mà câu
    /// dẫn của hai bảng khác nhau thì na ná nhau, nên để nó vào sổ là dựng sẵn một vụ chặn oan cho lượt
    /// bày bảng kế tiếp.
    /// </summary>
    private static bool CarriesTable(AgentConversation turn)
        => turn.ColumnMap != null
           || turn.PermissionMatrix != null
           || turn.FlowMap != null
           || turn.ScreenScopeMap != null
           || turn.EntityMap != null
           || turn.ReportMap != null
           || turn.NotificationMap != null;

    /// <summary>
    /// VẾ HỎI của một lượt BA — phần duy nhất đáng đem so khi hỏi "câu này hỏi rồi hay chưa".
    ///
    /// <para>
    /// <b>Vì sao không so cả <c>Message</c>.</b> Prompt bắt BA <i>phát lại điều đã ghi nhận rồi mới hỏi</i>
    /// (<c>requirement-chat.v4.md</c> § "QUY TẮC PHÁT LẠI") — nên một lượt hỏi một câu gần như luôn có dạng
    /// <i>"Cảm ơn anh/chị! Mình đã ghi nhận: … . &lt;câu hỏi&gt;?"</i>, và phần phát lại ĐỔI theo từng lượt vì
    /// nó chép lời người dùng vừa nói. Đem cả khối đó đi so là pha loãng đúng vế cần so: hai lượt hỏi CÙNG
    /// một câu vẫn lệch nhau vì hai câu phát lại khác nhau. Phanh chống hỏi lại vì thế câm ở đúng chỗ prompt
    /// làm đúng nhất.
    /// </para>
    /// <para>
    /// Ca thật (dự án <i>JD Libary 4</i>, lượt 16 → 18 → 20): lượt 20 tóm tắt câu trả lời người dùng vừa gõ
    /// ở lượt 19 rồi hỏi lại CHÍNH câu của lượt 16 — <i>"khi JD đã available và được gán cho nhân viên, nếu
    /// cần chỉnh sửa thì xử lý thế nào?"</i> — kèm một chip chép lại đúng câu trả lời đó. So nguyên
    /// <c>Message</c> cho bao phủ 0.68; so vế hỏi cho 1.00.
    /// </para>
    /// <para>
    /// Phép cắt: lấy tới dấu hỏi CUỐI (phần sau nó không phải câu hỏi), rồi lùi về sau dấu kết câu gần nhất
    /// — câu hỏi luôn đứng cuối theo đúng khuôn prompt. Bỏ nốt mệnh đề dẫn kết bằng dấu hai chấm
    /// (<i>"Mình còn một điểm cần làm rõ:"</i>) khi phần còn lại vẫn đủ dài để tự đứng.
    /// </para>
    /// <para>
    /// <b>Vế hỏi quá ngắn thì GIỮ NGUYÊN cả message.</b> Hai lượt khác hẳn nhau vẫn có thể cùng kết bằng
    /// <i>"Đúng không ạ?"</i>; cắt xuống còn bấy nhiêu là dựng ra một vụ trùng khoá TUYỆT ĐỐI giữa hai lượt
    /// không liên quan — đắt hơn hẳn việc bỏ sót, vì khớp tuyệt đối không có ngưỡng nào đỡ.
    /// </para>
    /// </summary>
    public static string QuestionCore(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        var end = Math.Max(text.LastIndexOf('?'), text.LastIndexOf('\uff1f'));
        if (end < 0)
            return text;

        var upToQuestion = text[..(end + 1)];
        var start = upToQuestion.LastIndexOfAny(SentenceEnders, upToQuestion.Length - 2);
        var core = (start >= 0 ? upToQuestion[(start + 1)..] : upToQuestion).Trim();

        var lead = core.LastIndexOf(':');
        if (lead >= 0 && core[(lead + 1)..].Trim().Length >= MinLengthForFuzzyMatch)
            core = core[(lead + 1)..].Trim();

        return Key(core).Length >= MinLengthForFuzzyMatch ? core : text;
    }

    // Dấu kết câu đứng TRƯỚC câu hỏi cuối. '?' có mặt vì một lượt được phép chở nhiều câu hỏi liên tiếp —
    // khi đó vế cần so là câu cuối cùng.
    private static readonly char[] SentenceEnders = { '.', '!', '?', '\uff1f', '\n' };

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

    /// <summary>
    /// Khoá của mọi câu đã hỏi — truyền vào <see cref="IsRepeat"/> để không chuẩn hoá lặp lại. Vế hỏi được
    /// cắt Ở ĐÂY, cùng chỗ với <see cref="IsRepeat"/>: hai phía của phép so bắt buộc phải cùng một hình
    /// dạng, và <see cref="Collect"/> thì cố ý giữ nguyên văn cho khối ngữ cảnh.
    /// </summary>
    public static HashSet<string> Keys(IEnumerable<string> asked)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in asked)
        {
            var key = Key(QuestionCore(question));
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
        var key = Key(QuestionCore(candidate));
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
    /// Ghi chú đánh dấu một dòng bản đồ mà NGƯỜI DÙNG vừa nói là BA hiểu chưa đúng. Lượt chắt lọc bản đồ
    /// ghi nguyên văn cụm này vào phần <c>còn thiếu:</c> của dòng đó — xem
    /// <c>Prompts/BusinessAnalyst/requirement-coverage.v3.md</c> § "Người dùng đính chính một nhóm".
    /// Là hằng số dùng chung vì <see cref="ReopenedGroups"/> đọc nó để MIỄN phanh chống-hỏi-lại cho đúng
    /// nhóm đó. <c>PromptReopenNoteRuleTests</c> giữ prompt và hằng số này không trôi khỏi nhau.
    /// </summary>
    public const string ReopenNote = "người dùng báo phần này chưa đúng";

    /// <summary>
    /// Các nhãn nhóm ĐƯỢC PHÉP hỏi lại: nhóm người dùng vừa đính chính trong chat ("nhóm này BA hiểu
    /// chưa đúng"), nhận ra qua <see cref="ReopenNote"/> trong tóm tắt dòng.
    /// Không có ngoại lệ này thì phanh chống-hỏi-lại sẽ chặn đúng cái đường mà người dùng vừa chủ động
    /// mở ra — họ bảo "nhóm này BA hiểu sai, hỏi lại giúp tôi" mà BA không được phép hỏi lại.
    /// </summary>
    public static HashSet<string> ReopenedGroups(IEnumerable<CoverageMapItem> coverage)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in coverage)
        {
            if (item.Summary.Contains(ReopenNote, StringComparison.OrdinalIgnoreCase))
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
