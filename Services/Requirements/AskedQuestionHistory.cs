using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sổ "những câu BA ĐÃ HỎI" của một hội thoại — nguồn dữ liệu của phanh chống hỏi lại.
///
/// <para>
/// Lý do tồn tại: thứ dẫn dắt lượt hỏi là bản đồ bao phủ — mà bản đồ chỉ có độ phân giải theo NHÓM (12
/// dòng), không theo câu hỏi. Một dòng chưa đạt chuẩn <c>[RÕ]</c> (chuẩn cố ý khắt khe trong
/// <c>requirement-coverage.v5.md</c>) đồng nghĩa "ưu tiên hỏi nhóm này", và vì mỗi câu hỏi trong lượt gộp
/// được yêu cầu gắn <c>group</c> = tên dòng bản đồ, model sinh lại ĐÚNG câu hỏi mở cũ của chính nhóm đó.
/// Người dùng vừa trả lời xong đã bị hỏi lại — kèm chip gợi ý chính là câu trả lời họ vừa gõ. Cùng chuyện
/// đó xảy ra khi lượt chắt lọc bản đồ lỗi (fail-open giữ bản cũ): bản đồ không nhúc nhích thì cả cụm câu
/// hỏi của lượt trước được phát lại y nguyên.
/// </para>
/// <para>
/// <b>Lớp này chỉ còn làm phần HÌNH DẠNG.</b> Nó gom sổ (<see cref="Collect"/>), nhận ra lượt nào đang hỏi
/// (<see cref="IsAskingTurn"/>) và bắt câu phát lại NGUYÊN VĂN (<see cref="IsRepeat"/> — khớp khoá tuyệt
/// đối, không phụ thuộc ngôn ngữ, không tốn lời gọi nào). Câu hỏi lại đã được VIẾT LẠI — rụng vài chữ, có
/// thêm đoạn kể lại đứng trước, hay một câu hỏi-vét thay vế liệt kê — là chuyện của NGHĨA và do
/// <see cref="InterviewJudge"/> phán.
/// </para>
/// <para>
/// ĐỪNG TÌM <c>QuestionCore</c>, <c>IsSweepRepeat</c>, <c>IsBareConfirmation</c> hay hai ngưỡng
/// bao phủ/Jaccard: chúng đo "hỏi lại" bằng tập từ trùng cộng bốn bảng cụm tiếng Việt (tiểu từ lịch sự,
/// cụm xin gật, cụm hỏi-vét, cụm mở đầu ví dụ) và đã được gỡ khi lượt trọng tài ra đời. Bảng cụm lớn dần
/// theo từng ca lọt lưới, và ở một hội thoại không phải tiếng Việt thì nó không chỉ câm mà còn chặn oan:
/// hai lượt xin xác nhận khác nhau cùng kết bằng <i>"Is my understanding correct?"</i> bị coi là một.
/// </para>
/// <para>
/// Lớp này KHÔNG nạp gì vào prompt của lượt chat: model đọc lại các câu nó đã hỏi từ CHÍNH transcript —
/// xem ghi chú ở <c>BAChatService.BuildMessagesAsync</c>.
/// </para>
/// </summary>
public static class AskedQuestionHistory
{
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
            if (message.StartsWith(RequirementScreenText.LlmFailurePrefix, StringComparison.Ordinal))
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
            // Sổ này giữ NGUYÊN VĂN lượt đã hỏi, không cắt vế hỏi: cái được đem so — bằng khoá tuyệt
            // đối ở <see cref="IsRepeat"/> lẫn bằng nghĩa ở <see cref="InterviewJudge"/> — phải đúng là
            // câu đã lên màn hình.
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
    /// chỉ có khi model tự đặt (<c>BAChatReplyParser</c> không đoán hộ). Hệ quả: một câu hỏi KHÔNG chip mà
    /// model cũng quên đánh dấu được ghi vào sổ nhưng không bao giờ bị soi lại — nó chảy thẳng lên màn
    /// hình dù đã hỏi rồi. Ca thật (dự án quản lý khóa học bắt buộc, lượt 38
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
    /// Lượt bày một BẢNG CHỐT (phân quyền, luồng, màn hình, đối tượng, báo cáo, thông báo). Đứng ngoài sổ:
    /// chỗ trả lời của lượt đó là chính cái bảng, còn `Message` chỉ là câu dẫn — mà câu dẫn của hai bảng
    /// khác nhau thì na ná nhau, nên để nó vào sổ là dựng sẵn một vụ chặn oan cho lượt bày bảng kế tiếp.
    ///
    /// <para>
    /// Lượt đọc file mang BẢNG CỘT thì KHÔNG đứng ngoài sổ: bảng đó đã được lưu và chỉ thu gọn trong bong
    /// bóng, còn lượt kết bằng một câu hỏi khai thác thật — bỏ nó khỏi sổ là để phanh chống-hỏi-lại không
    /// nhìn thấy đúng câu BA vừa hỏi.
    /// </para>
    /// </summary>
    private static bool CarriesTable(AgentConversation turn)
        => turn.PermissionMatrix != null
           || turn.FlowMap != null
           || turn.ScreenScopeMap != null
           || turn.ReportMap != null
           || turn.NotificationMap != null;


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
    /// Khoá của mọi câu đã hỏi — truyền vào <see cref="IsRepeat"/> để không chuẩn hoá lặp lại. Khoá lấy
    /// trên NGUYÊN câu đã lên màn hình, không cắt lấy "vế hỏi": cắt là một phán đoán về nghĩa (vế nào chở
    /// nội dung), và phán đoán đó nay thuộc về <see cref="InterviewJudge"/>.
    /// </summary>
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
    /// <paramref name="candidate"/> có phải một câu đã hỏi được phát lại NGUYÊN VĂN không (trùng khoá sau
    /// khi bỏ dấu câu, khoảng trắng, hoa/thường). Đây là lưới TẤT ĐỊNH của phanh chống hỏi lại: nó chạy cả
    /// khi lượt trọng tài lỗi, và nó không bao giờ chặn oan — hai câu trùng từng chữ thì là một câu.
    /// </summary>
    public static bool IsRepeat(string? candidate, IReadOnlyCollection<string> askedKeys)
    {
        var key = Key(candidate);
        return key.Length > 0 && askedKeys.Contains(key);
    }

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

}
