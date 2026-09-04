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
/// khắt khe trong <c>requirement-coverage.v5.md</c>) đồng nghĩa "ưu tiên hỏi nhóm này", và vì mỗi câu
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

            // Lượt hỏi một câu: Message CHỞ câu hỏi. Nhận diện bằng <see cref="IsAskingTurn"/> — phép thử
            // DÙNG CHUNG với phía đối chiếu, xem ghi chú ở đó về việc vì sao hai phía bắt buộc phải là
            // một.
            //
            // Sổ này giữ NGUYÊN VĂN lượt đã hỏi, không cắt vế hỏi: nó còn là khối "các câu BẠN ĐÃ HỎI" nạp
            // vào ngữ cảnh (<see cref="BuildNote"/>), và ở đó model cần đọc đúng câu như nó đã lên màn hình.
            // Việc cắt là chuyện của phép SO KHỚP, làm bên trong <see cref="Keys"/>/<see cref="IsRepeat"/>.
            if (message.Length > 0
                && IsAskingTurn(message, ConversationTurnRenderer.ParseSuggestions(turn.Suggestions).Count > 0)
                && !CarriesTable(turn))
                asked.Add(message);
        }

        return asked;
    }

    /// <summary>
    /// Lượt này có phải một lượt HỎI không — phép thử DÙNG CHUNG cho hai phía của phanh chống hỏi lại:
    /// phía GHI SỔ (<see cref="Collect"/>, đọc các lượt đã lưu) và phía ĐỐI CHIẾU
    /// (<c>BAChatService.ApplyRepeatedQuestionBrake</c>, soi lượt model vừa trả về).
    ///
    /// <para>
    /// <b>Hai phía lệch nhau thì phanh câm ở đúng những lượt nó cần bắt.</b> Phía ghi sổ vốn nhận diện
    /// bằng "có chip HOẶC có dấu hỏi", còn phía đối chiếu bằng "có chip HOẶC là câu mở" — mà cờ "câu mở"
    /// chỉ bật khi câu chứa một cụm xin-kể (<c>BAChatReplyParser.NarrativeCues</c>: "kể giúp", "mô tả"…).
    /// Hệ quả: một câu hỏi KHÔNG chip và KHÔNG mang cụm xin-kể được ghi vào sổ nhưng không bao giờ bị soi
    /// lại — nó chảy thẳng lên màn hình dù đã hỏi rồi. Ca thật (dự án quản lý khóa học bắt buộc, lượt 38
    /// và lượt cuối): *"ngoài việc khóa học hết hạn, còn có trường hợp nào khác cần xử lý không?"* quay
    /// lại thành *"ngoài việc nhân viên nghỉ việc và chuyển vai trò, còn có trường hợp nào khác cần xử lý
    /// không?"* — cả hai lượt đều <c>suggestions: []</c>, nên phanh không chạy lần nào.
    /// </para>
    ///
    /// <para>
    /// <paramref name="hasAnswerAffordance"/> = lượt này có bày sẵn CHỖ TRẢ LỜI không (bộ chip; ở phía
    /// đối chiếu tính cả cờ "câu mở", thứ không đọc lại được từ một lượt đã lưu). Dấu hỏi là vế thứ hai
    /// và là ranh giới duy nhất còn lại: lượt tóm tắt/thông báo không có nó nên vẫn đứng ngoài.
    /// </para>
    /// </summary>
    public static bool IsAskingTurn(string? message, bool hasAnswerAffordance)
        => hasAnswerAffordance || AsksSomething(message ?? string.Empty);

    /// <summary>Lượt này có dấu hỏi không — ranh giới duy nhất đọc được từ một lượt đã lưu.</summary>
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
        if (LastQuestionSentence(text) is not { } sentence)
            return text;

        var core = StripLeadClause(sentence.Sentence);
        return Key(core).Length >= MinLengthForFuzzyMatch && !IsBareConfirmation(core) ? core : text;
    }

    /// <summary>
    /// CÂU hỏi cuối cùng của <paramref name="text"/> (nguyên văn, CHƯA bỏ mệnh đề dẫn) kèm vị trí nó bắt
    /// đầu — <c>null</c> khi không có dấu hỏi nào. Tách riêng vì hai phép thử cần đúng một định nghĩa "câu
    /// hỏi cuối": <see cref="QuestionCore"/> lấy nó làm vế đem so, còn <see cref="SweepOwner"/> phải lùi
    /// thêm một câu nữa nên cần biết câu cuối bắt đầu ở đâu.
    /// </summary>
    private static (string Sentence, int Start)? LastQuestionSentence(string text)
    {
        var end = Math.Max(text.LastIndexOf('?'), text.LastIndexOf('\uff1f'));
        if (end < 0)
            return null;

        var upToQuestion = text[..(end + 1)];
        // Lùi từ ký tự TRƯỚC dấu hỏi: chính dấu hỏi cũng là một dấu kết câu, dò từ nó thì câu nào cũng rỗng.
        var cut = upToQuestion.Length >= 2
            ? upToQuestion.LastIndexOfAny(SentenceEnders, upToQuestion.Length - 2)
            : -1;
        var start = cut >= 0 ? cut + 1 : 0;
        return (upToQuestion[start..].Trim(), start);
    }

    /// <summary>
    /// Bỏ mệnh đề dẫn kết bằng dấu hai chấm (<i>"Anh/chị cho mình biết:"</i>, <i>"Ví dụ:"</i>) khi phần còn
    /// lại vẫn đủ dài để tự đứng.
    /// </summary>
    private static string StripLeadClause(string sentence)
    {
        var lead = sentence.LastIndexOf(':');
        return lead >= 0 && sentence[(lead + 1)..].Trim().Length >= MinLengthForFuzzyMatch
            ? sentence[(lead + 1)..].Trim()
            : sentence;
    }

    /// <summary>
    /// Vế hỏi CHỈ xin một tiếng gật cho đoạn phát lại đứng ngay trước nó — *"Anh/chị thấy mình hiểu vậy
    /// đã đúng chưa?"*, *"Mình chốt vậy đúng không ạ?"*. Nó KHÔNG phân biệt được hai lượt: nội dung nằm
    /// trọn ở đoạn phát lại vừa bị cắt đi, nên hai lượt CHỐT LẠI hai điều khác hẳn nhau vẫn kết bằng đúng
    /// một câu ấy — lấy nó làm khoá là dựng sẵn một vụ trùng khoá TUYỆT ĐỐI giữa hai lượt không liên quan.
    /// Cùng cái bẫy với "vế hỏi quá ngắn" ở <see cref="QuestionCore"/>, chỉ khác là câu này dài hơn
    /// <see cref="MinLengthForFuzzyMatch"/> nên ngưỡng kia không đỡ; cách xử cũng vậy — giữ NGUYÊN cả
    /// message làm khoá, vì phần phát lại mới là thứ phân biệt hai lượt.
    ///
    /// <para>
    /// Hẹp có chủ ý ở CẢ HAI vế. Cụm xác nhận phải nằm ở CUỐI (một câu chỉ nhắc tới nó giữa chừng vẫn là
    /// câu hỏi thật), và cả vế hỏi phải ngắn hơn <see cref="MaxBareConfirmationLength"/>: một kịch bản
    /// mẫu hay một ví dụ tính thử xin chốt (*"…3 mục tiêu 80/90/70 trọng số 50/30/20 thì tổng 81 điểm —
    /// đúng cách anh/chị tính không?"*) cũng kết bằng cụm xác nhận, nhưng nó CHỞ nội dung nên phải được
    /// đem so như một câu hỏi bình thường.
    /// </para>
    /// </summary>
    private static bool IsBareConfirmation(string core)
    {
        var key = Key(core);
        return key.Length <= MaxBareConfirmationLength
               && ConfirmationCues.Any(cue => key.EndsWith(cue, StringComparison.Ordinal));
    }

    // Cụm xác nhận đứng cuối một lượt phát-lại-rồi-xin-gật. Cố ý KHÔNG nhận "không" đứng một mình —
    // cùng lý do với YesNoCues: nó có mặt ở cuối vô số câu hỏi thật.
    private static readonly string[] ConfirmationCues =
    {
        "đúng chưa", "đúng không", "đúng không ạ", "đúng chứ", "phải không", "phải vậy không", "đúng ý chưa"
    };

    // Trần độ dài (sau chuẩn hoá) để một vế hỏi kết bằng cụm xác nhận bị coi là "không chở nội dung".
    // Đo trên các khuôn prompt bắt BA dùng: câu xin gật trần ("anh chị thấy mình hiểu vậy đã đúng chưa"
    // — 39) nằm dưới ngưỡng, còn kịch bản mẫu và ví dụ tính thử — hai thứ prompt BẮT BUỘC phải chở đủ
    // dữ kiện để người dùng soát — đều trên 90.
    private const int MaxBareConfirmationLength = 56;

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
    /// Các CHIP đã bày ra ở một lượt chọn-nhiều mà lượt user ngay sau đó KHÔNG chọn — tức người dùng đã
    /// trả lời "cái này thì không". Trả về khoá đã chuẩn hoá (<see cref="Key"/>) để caller so trực tiếp.
    ///
    /// <para>
    /// <b>Vì sao một chip không được chọn cũng là một câu trả lời.</b> Ở lượt <c>multiSelect</c>, bộ chip
    /// là cả một danh sách bày sẵn và người dùng tích những mảnh đúng với họ; mảnh không tích mang đúng
    /// nghĩa "không có cái này". Sổ "đã hỏi" thường (<see cref="Collect"/>) không thấy điều đó — nó chỉ ghi
    /// CÂU HỎI — nên BA quay lại hỏi riêng đúng cái mảnh vừa bị bỏ, và người dùng phải trả lời lần thứ hai
    /// cho cùng một thông tin. Ca thật (dự án JD Libary 5, lượt 14→16): lượt 14 bày
    /// <c>["Ngày gán JD", "Nhân viên được gán", "Ngày hiệu lực", "Ngày hết hạn"]</c> ở chế độ chọn nhiều,
    /// người dùng liệt kê ba cái đầu; lượt 16 hỏi lại *"có cần lưu thêm ngày hết hạn hay không?"* — đốt
    /// trọn một lượt để nghe lại đúng một tiếng "không".
    /// </para>
    ///
    /// <para>
    /// Chỉ xét lượt <c>multiSelect</c>: ở lượt chọn-MỘT, các chip còn lại là những phương án bị loại theo
    /// luật của câu hỏi chứ không phải những thứ người dùng "đã bỏ", nên hỏi lại một phương án khác vẫn có
    /// thể là một câu hỏi mới hợp lệ.
    /// </para>
    /// </summary>
    public static HashSet<string> DeclinedChipKeys(IReadOnlyList<AgentConversation> turns)
    {
        var declined = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (!ConversationTurnRenderer.IsAssistant(turn) || !turn.SuggestionsMultiSelect)
                continue;

            var chips = ConversationTurnRenderer.ParseSuggestions(turn.Suggestions);
            if (chips.Count == 0)
                continue;

            // Lượt user NGAY SAU đó là câu trả lời cho bộ chip này. Không có lượt nào sau (bộ chip đang
            // chờ trả lời) ⇒ chưa có gì bị bỏ.
            var answer = turns.Skip(i + 1).FirstOrDefault(t => !ConversationTurnRenderer.IsAssistant(t));
            if (answer == null)
                continue;

            var answerKey = Key(answer.Message);
            foreach (var chip in chips)
            {
                var chipKey = Key(chip);
                if (chipKey.Length > 0 && !answerKey.Contains(chipKey, StringComparison.Ordinal))
                    declined.Add(chipKey);
            }
        }

        return declined;
    }

    /// <summary>
    /// Câu hỏi này có phải đang hỏi CÓ/KHÔNG về đúng một chip mà người dùng vừa bỏ không — nếu phải thì nó
    /// là câu hỏi lại, dù mặt chữ khác hẳn câu đã hỏi nên <see cref="IsRepeat"/> không bắt được.
    ///
    /// <para>
    /// Hai điều kiện phải cùng đạt, và cả hai đều hẹp có chủ ý:
    /// <list type="bullet">
    /// <item>câu hỏi có hình dạng CÓ/KHÔNG — nó chỉ xin một tiếng gật hay lắc, tức đúng thứ cú bấm vừa
    /// rồi đã trả lời;</item>
    /// <item>và nó chở nguyên văn một chip đã bị bỏ.</item>
    /// </list>
    /// Thiếu vế đầu thì một câu ĐÀO SÂU về cùng chủ đề (*"ngày hết hạn của một lần gán do ai đặt?"*) cũng
    /// bị chặn oan — mà đó lại đúng là việc BA nên làm.
    /// </para>
    /// </summary>
    public static bool AsksAboutDeclinedChip(string? candidate, IReadOnlyCollection<string> declinedChipKeys)
    {
        if (declinedChipKeys.Count == 0)
            return false;

        var text = QuestionCore(candidate)?.ToLowerInvariant() ?? string.Empty;
        if (!YesNoCues.Any(cue => text.Contains(cue, StringComparison.Ordinal)))
            return false;

        var key = Key(text);
        return key.Length > 0 && declinedChipKeys.Any(chip => key.Contains(chip, StringComparison.Ordinal));
    }

    // Hình dạng CÓ/KHÔNG của một câu hỏi tiếng Việt. Cố ý không nhận "không" đứng một mình: nó có mặt
    // trong vô số câu hỏi mở ("chỗ nào chưa đúng hoặc còn thiếu?").
    private static readonly string[] YesNoCues =
    {
        "có cần", "có phải", "hay không", "có lưu", "có dùng", "có áp dụng", "có bắt buộc", "đúng không"
    };

    /// <summary>
    /// Câu HỎI-VÉT đã hỏi rồi: *"ngoài &lt;những thứ đã biết&gt;, còn có &lt;cái gì&gt; nào KHÁC không?"*.
    ///
    /// <para>
    /// <b>Vì sao nó cần một phép thử riêng.</b> Câu hỏi-vét không hỏi một điều mới — nó hỏi PHẦN CÒN LẠI
    /// của đúng nhóm vừa hỏi. Khi phát lại, model giữ nguyên khung câu và chỉ thay vế "ngoài …" bằng câu
    /// trả lời nó vừa nhận, nên hai lượt lệch nhau đúng ở chỗ liệt kê. Ca thật (dự án quản lý khóa học
    /// bắt buộc): *"ngoài việc khóa học hết hạn, còn có trường hợp nào khác cần xử lý không?"* → *"ngoài
    /// việc nhân viên nghỉ việc và chuyển vai trò, còn có trường hợp nào khác cần xử lý không?"*. Đo bằng
    /// <see cref="IsRepeat"/>: bao phủ 0.75 và Jaccard 0.52 — dưới CẢ HAI ngưỡng, vì phần đổi chiếm gần
    /// nửa số từ. Hạ ngưỡng để bắt nó thì chặn oan hàng loạt câu đào sâu thật (xem ghi chú ở
    /// <see cref="RepeatJaccard"/>), nên chỗ để bắt là HÌNH DẠNG, không phải độ tương đồng.
    /// </para>
    ///
    /// <para>
    /// Ba điều kiện phải cùng đạt, và cả ba đều hẹp có chủ ý: câu phải mang một cụm VÉT
    /// (<see cref="SweepCues"/> — "nào khác", "gì nữa"…, chứ không phải chữ "nào" đứng một mình, thứ có
    /// trong mọi câu hỏi mở); phải có vế liệt kê ngăn bằng dấu phẩy để mà thay; và ĐUÔI sau dấu phẩy cuối
    /// phải trùng KHÍT với đuôi một câu vét đã hỏi, dài tối thiểu <see cref="MinLengthForFuzzyMatch"/> —
    /// một đuôi cụt kiểu *"ai xử lý?"* thì hai câu khác hẳn nhau cũng đụng nhau.
    /// </para>
    ///
    /// <para>
    /// Điều kiện thứ tư nằm ở <see cref="SweepOwner"/>: khi mệnh đề vét chỉ là danh sách VÍ DỤ treo sau
    /// một câu hỏi khác, chủ thể của câu hỏi nằm ở câu trước chứ không ở trong đuôi, nên khoá phải chở
    /// thêm câu ấy — nếu không, phanh chặn oan mọi lượt hỏi một chủ thể MỚI bằng cùng một khuôn câu.
    /// </para>
    /// </summary>
    public static bool IsSweepRepeat(string? candidate, IReadOnlyCollection<string> sweepTails)
        => sweepTails.Count > 0 && SweepTail(candidate) is { } tail && sweepTails.Contains(tail);

    /// <summary>Đuôi vét của mọi câu đã hỏi — truyền vào <see cref="IsSweepRepeat"/>. Câu không phải hỏi-vét không góp gì.</summary>
    public static HashSet<string> SweepTailKeys(IEnumerable<string> asked)
    {
        var tails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in asked)
        {
            if (SweepTail(question) is { } tail)
                tails.Add(tail);
        }
        return tails;
    }

    /// <summary>
    /// Khoá của một câu hỏi-vét: phần sau dấu phẩy CUỐI của vế hỏi, chuẩn hoá — và khi mệnh đề vét chỉ là
    /// một danh sách VÍ DỤ treo sau một câu hỏi khác, thêm chính câu hỏi ấy vào khoá
    /// (<see cref="SweepOwner"/>). <c>null</c> khi câu này không phải hỏi-vét.
    /// </summary>
    private static string? SweepTail(string? message)
    {
        var core = QuestionCore(message);
        if (core.Length == 0)
            return null;

        var lower = core.ToLowerInvariant();
        if (!SweepCues.Any(cue => lower.Contains(cue, StringComparison.Ordinal)))
            return null;

        var comma = core.LastIndexOf(',');
        if (comma < 0)
            return null;

        var tail = Key(core[(comma + 1)..]);
        if (tail.Length < MinLengthForFuzzyMatch)
            return null;

        var owner = SweepOwner(message);
        return owner.Length == 0 ? tail : owner + OwnerSeparator + tail;
    }

    /// <summary>
    /// Câu hỏi mà mệnh đề vét đang PHỤ THUỘC vào, khi mệnh đề ấy nằm trong một câu chỉ liệt kê ví dụ
    /// (<see cref="ExampleLeads"/>) đứng sau một câu hỏi khác. Rỗng ⇒ mệnh đề vét tự nó là câu hỏi, khoá
    /// chỉ cần cái đuôi như trước.
    ///
    /// <para>
    /// <b>Vì sao cái đuôi một mình là chưa đủ.</b> Khuôn <i>"&lt;ai đó&gt; sẽ dùng ứng dụng để làm những
    /// việc gì? Ví dụ: A, B, hay còn thao tác nào khác?"</i> đặt CHỦ THỂ của câu hỏi ở câu trước, còn cái
    /// đuôi thì là văn mẫu dùng lại cho mọi chủ thể. Hỏi vai <i>Nhân viên</i> sau khi đã hỏi vai
    /// <i>Quản lý trực tiếp</i> cho ra đúng cái đuôi ấy, và phanh chặn oan một câu hỏi hoàn toàn mới — ca
    /// thật ở dự án quản lý khóa học bắt buộc (2026-09-03): lượt hỏi vai Nhân viên bị thay bằng câu chặn
    /// của cổng, vai đó không được hỏi lượt nào. Ngược lại, câu vét THẬT
    /// (<i>"ngoài việc X, còn có trường hợp nào khác cần xử lý không?"</i>) chở chủ thể ngay trong đuôi và
    /// không có câu hỏi nào đứng trước ⇒ khoá của nó không đổi, phanh vẫn bắt như cũ.
    /// </para>
    /// </summary>
    private static string SweepOwner(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (LastQuestionSentence(text) is not { } last)
            return string.Empty;

        var sentenceKey = Key(last.Sentence);
        if (!ExampleLeads.Any(cue => sentenceKey.StartsWith(cue, StringComparison.Ordinal)))
            return string.Empty;

        // Câu liệt kê ví dụ đứng đầu lượt thì không phụ thuộc vào câu nào — giữ khoá đuôi như cũ.
        return LastQuestionSentence(text[..last.Start].TrimEnd()) is { } owner
            ? Key(StripLeadClause(owner.Sentence))
            : string.Empty;
    }

    // Ngăn giữa hai vế của khoá vét. Là chuỗi KHÔNG thể sinh ra từ Key() (nó chỉ giữ chữ, số và khoảng
    // trắng) nên không khoá đuôi nào tự nhiên đụng phải.
    private const string OwnerSeparator = " | ";

    // Cụm mở đầu một câu chỉ LIỆT KÊ ví dụ cho câu hỏi ngay trước nó. So trên khoá đã chuẩn hoá nên dấu
    // hai chấm/dấu phẩy sau cụm không ảnh hưởng; khoảng trắng ở cuối để "vd" không nuốt "vdt…".
    private static readonly string[] ExampleLeads = { "ví dụ ", "vd ", "chẳng hạn ", "thí dụ " };

    // Cụm báo hiệu một câu VÉT phần còn lại. Đều phải là hai chữ: "nào"/"gì" đứng một mình có trong mọi
    // câu hỏi mở, nhận chúng là biến phép thử này thành một cái lưới quét sạch.
    private static readonly string[] SweepCues =
    {
        "nào khác", "nào nữa", "gì khác", "gì nữa", "còn thiếu gì", "còn sót"
    };

    /// <summary>
    /// Ghi chú đánh dấu một dòng bản đồ mà NGƯỜI DÙNG vừa nói là BA hiểu chưa đúng. Lượt chắt lọc bản đồ
    /// ghi nguyên văn cụm này vào phần <c>còn thiếu:</c> của dòng đó — xem
    /// <c>Prompts/BusinessAnalyst/requirement-coverage.v5.md</c> § "Người dùng đính chính một nhóm".
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
