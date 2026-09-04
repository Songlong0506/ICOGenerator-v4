using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH cho CHẤT LƯỢNG của danh sách câu hỏi (<see cref="OpenQuestionDocument"/>): cuộc
/// phỏng vấn không được mang một "câu hỏi" mà người dùng đọc lên không biết phải trả lời gì. Chạy ở đường
/// GHI, ngay sau <see cref="CoverageStaleGapGuard"/>.
///
/// <para>
/// <b>Vì sao phải là một cái phanh chứ không chỉ là luật trong prompt.</b>
/// <c>requirement-coverage.v5.md</c> đã ghi luật "câu hỏi phải hỏi được một điều cụ thể", và
/// <see cref="RequirementReadinessGate"/> đã có một phép thử cho câu rỗng nghĩa — nhưng phép thử đó nằm ở
/// ĐƯỜNG ĐỌC, tức chỉ cứu được đúng một chỗ tiêu thụ. Câu hỏi hỏng vẫn nằm nguyên trong DB, vẫn đi vào ngữ
/// cảnh CHAT ở mọi lượt sau qua <see cref="BAChatPromptBlocks"/>, vẫn hiện trên tooltip của panel
/// "Tiến độ khai thác", và vẫn được chính lượt distill kế tiếp đọc lại rồi chép sang danh sách mới. Lọc ở
/// đường ghi thì mọi tầng thấy CÙNG một sự thật — đúng lý do <see cref="CoveragePendingGuard"/> chọn chạy
/// ở đường ghi.
/// </para>
///
/// <para>
/// <b>Ca thật (dự án quản lý khóa học bắt buộc).</b> Dòng «Thông báo / nhắc nhở» đứng <c>[MỘT PHẦN]</c> với
/// câu hỏi <i>"Bảng thông báo theo sự kiện chưa được chốt."</i> — một câu MÔ TẢ TRẠNG THÁI HỆ
/// THỐNG, không hỏi ai điều gì. Cổng "Write Requirement" phát nguyên văn nó thành
/// <i>"Anh/chị cho mình hỏi thêm: bảng thông báo theo sự kiện chưa được chốt — anh/chị cho mình xin thông
/// tin này nhé?"</i>: người dùng không có cách nào trả lời, và cả BA cũng không đọc ra được phải hỏi gì.
/// Tệ hơn, nhóm ấy là nhóm CHỐT BẰNG BẢNG — BA bị cấm hỏi lẻ nó, nên không có lượt chat nào đóng câu đó
/// lại được; đường đúng là <see cref="NotificationMapGate"/> bày bảng ra rồi
/// <see cref="CoverageConfirmedTableGuard"/> nâng dòng lên <c>[RÕ]</c>.
/// </para>
///
/// <para>
/// <b>Ba hình dạng bị XOÁ</b> (chỉ xoá câu hỏi, xem luật một chiều bên dưới):
/// <list type="number">
///   <item>RỖNG NGHĨA — *"các quy tắc khác (nếu có)"*, *"thông tin bổ sung"*, *"các điểm còn lại"*: một danh
///         từ mê-ta chỉ CHỖ của câu trả lời chứ không chở câu hỏi nào (<see cref="IsHollow"/>).</item>
///   <item>MÔ TẢ TRẠNG THÁI HỆ THỐNG — câu kết thúc bằng *"chưa được chốt"*, *"chưa xác định"*, *"chưa có
///         thông tin"*: nó nói về cái BẢNG/HỆ THỐNG chứ không hỏi người dùng (<see cref="IsStateReport"/>).
///         Nhận diện theo ĐUÔI câu nên *"chưa rõ ai duyệt đơn thay trưởng phòng"* — mở đầu bằng "chưa rõ"
///         nhưng chở đúng một câu hỏi — vẫn sống.</item>
///   <item>HAI NHÓM CHỐT BẰNG BẢNG — «Phân quyền theo nghiệp vụ» và «Thông báo / nhắc nhở»: BA bị cấm hỏi lẻ
///         chúng, nên mọi câu hỏi gắn vào hai dòng ấy đều là câu hỏi CHẾT dù viết hay tới đâu
///         (<see cref="IsTableDecidedGroup"/>).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Một chiều: chỉ XOÁ câu hỏi, KHÔNG BAO GIỜ đụng trạng thái.</b> Cùng luật với
/// <see cref="CoverageStaleGapGuard"/> và cùng lý do: guard không đọc được hội thoại nên không có tư cách
/// kết luận "nhóm này đã đủ". Dòng mất câu hỏi vẫn đứng <c>[MỘT PHẦN]</c> và cổng rơi về nhánh PHÁT LẠI
/// (*"Mình đang ghi nhận: … Phần này còn chỗ nào chưa đúng hoặc còn thiếu không?"*) — một câu ĐÓNG LẠI ĐƯỢC
/// bằng một lượt, thay cho một câu không có câu trả lời nào đúng. Quyền nâng <c>[RÕ]</c> vẫn nằm ở lượt
/// distill kế tiếp, quyền nâng hai dòng chốt-bằng-bảng vẫn nằm ở
/// <see cref="CoverageConfirmedTableGuard"/>.
/// </para>
///
/// <para>
/// <b>Dòng mang cụm <see cref="AskedQuestionHistory.ReopenNote"/> đứng NGOÀI cả ba luật.</b> Đó là người
/// dùng vừa nói BA hiểu sai nhóm này — đúng một lần họ chủ động mở lại đường hỏi, kể cả với hai nhóm
/// chốt-bằng-bảng. Và cụm ấy còn là TÍN HIỆU MÁY: <see cref="AskedQuestionHistory.ReopenedGroups"/> đọc nó
/// (qua <see cref="CoverageMapItem.Summary"/>) để miễn phanh chống-hỏi-lại cho nhóm đó, nên xoá ô này là
/// cướp mất cái đường ấy trong im lặng. Cùng chỗ cố ý không đụng của
/// <see cref="CoverageConfirmedTableGuard"/>.
/// </para>
/// </summary>
public static class CoverageQuestionGuard
{
    /// <summary>
    /// Ghi chép CŨ mà lượt distill đính vào cuối câu hỏi của một dòng vừa bị đính chính — dành cho BA đọc,
    /// không phải điều cần hỏi. Hằng số dùng chung với <see cref="RequirementReadinessGate"/>: hai bản chép
    /// tay thì lần sửa sau chỉ sửa một bản.
    /// </summary>
    public const string RecordedNote = "(ghi nhận trước đó:";

    /// <summary>
    /// Xoá khỏi <paramref name="questions"/> mọi câu hỏi MỞ không dùng được. Chỉ đọc nhóm của chính mục đó
    /// nên guard này không cần bản đồ.
    /// </summary>
    public static void Apply(IList<OpenQuestionEntry> questions)
    {
        // Duyệt NGƯỢC để xoá tại chỗ mà không nhảy cóc phần tử.
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            var question = questions[i];
            if (!question.IsOpen)
                continue;

            // Người dùng vừa đính chính nhóm này ⇒ để nguyên cả mục: phần sau cụm tín hiệu là câu hỏi họ
            // vừa mở ra, và bản thân cụm tín hiệu còn được đọc để miễn phanh chống-hỏi-lại.
            if (question.Text.Contains(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsUsable(question.Text, question.Group))
                continue;

            questions.RemoveAt(i);
        }
    }

    /// <summary>
    /// Câu hỏi này bày ra màn hình được không — <c>false</c> nghĩa là phải xoá. Mở cho
    /// <see cref="RequirementReadinessGate"/> dùng lại ở đường đọc: danh sách của một dự án chỉ được lọc từ
    /// lượt distill kế tiếp trở đi, nên cổng vẫn phải tự vệ với thứ đang nằm sẵn trong DB.
    /// </summary>
    public static bool IsUsable(string? question, string? group)
    {
        var text = StripMachineNotes(question);
        return text.Length > 0 && !IsHollow(text) && !IsStateReport(text) && !IsTableDecidedGroup(group);
    }

    /// <summary>
    /// Phần HỎI THẬT của một mục: lược sạch hai ghi chú máy — cụm tái mở
    /// (<see cref="AskedQuestionHistory.ReopenNote"/>) và <see cref="RecordedNote"/> — rồi bỏ dấu câu thừa
    /// ở đuôi. Đây là thứ <see cref="RequirementReadinessGate"/> đọc ra màn hình, nên phép lược phải nằm ở
    /// MỘT chỗ cho cả đường ghi lẫn đường đọc.
    /// </summary>
    public static string StripMachineNotes(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return string.Empty;

        var text = question.Trim();

        var note = text.IndexOf(RecordedNote, StringComparison.OrdinalIgnoreCase);
        if (note >= 0)
            text = text[..note].Trim();

        return StripReopenMarker(text).TrimEnd('.', ';', ',');
    }

    /// <summary>
    /// Bỏ CÂU chứa cụm <see cref="AskedQuestionHistory.ReopenNote"/> và giữ phần distiller viết thêm sau nó
    /// (<c>requirement-coverage.v5.md</c> § "Người dùng đính chính một nhóm" bắt buộc viết tiếp đúng câu cần
    /// hỏi lại). Cụm ấy là TÍN HIỆU MÁY, đọc nguyên văn ra màn hình thì lượt chặn thành một câu rỗng nghĩa
    /// xưng "người dùng" ở ngôi thứ ba với chính người đang đọc — ca thật đã lên màn hình ở dự án JD Library
    /// lượt 34. Mở cho <see cref="RequirementReadinessGate"/> vì phần ĐÃ GHI NHẬN cũng phải lược cụm này.
    /// </summary>
    public static string StripReopenMarker(string text)
    {
        var at = text.IndexOf(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return text;

        var sentenceEnd = text.IndexOf('.', at);
        var tail = sentenceEnd >= 0 ? text[(sentenceEnd + 1)..] : string.Empty;
        return (text[..at] + " " + tail).Trim();
    }

    /// <summary>
    /// Câu hỏi không nói được đang hỏi cái gì — *"các quy tắc khác (nếu có)"*, *"thông tin bổ sung"*, *"các
    /// điểm còn lại"*. Nó là một CHỖ TRỐNG chứ không phải câu hỏi: distiller viết ra để dòng trông "chưa
    /// xong", nhưng cổng thì phát nguyên văn nó lên màn hình.
    ///
    /// <para>
    /// Ca thật (dự án JD Libary 5, lượt 26 — lượt CUỐI của buổi phỏng vấn): người dùng nhận
    /// *"Anh/chị cho mình hỏi thêm: các quy tắc khác (nếu có) — anh/chị cho mình xin thông tin này nhé?"*.
    /// Câu đó không trả lời được bằng một điều cụ thể nào, và tệ hơn: một tiếng *"không có"* sẽ lật dòng
    /// «Quy tắc nghiệp vụ &amp; ràng buộc» lên <c>[RÕ]</c> mà không thêm được một quy tắc nào — cổng mở ra
    /// bằng một câu hỏi rỗng.
    /// </para>
    ///
    /// <para>
    /// Nhận diện theo HÌNH DẠNG, cùng cách với chip "khác" trần ở <see cref="BAChatReplyParser"/>: bỏ phần
    /// trong ngoặc, bỏ từ chỉ số nhiều ở đầu, rồi hỏi phần còn lại có phải một danh từ MÊ-TA gắn đuôi
    /// "khác / còn lại / bổ sung" hay không. Danh sách đầu mê-ta cố ý HẸP — một câu chở danh từ nghiệp vụ
    /// thật (*"các trạng thái khác của JD"*) không lọt vào đây, và lọt lưới thì chỉ mất một lượt.
    /// </para>
    /// </summary>
    public static bool IsHollow(string question)
    {
        var text = question.ToLowerInvariant().Trim();

        // "(nếu có)", "(nếu cần)" — phần chú không bao giờ là nội dung cần hỏi.
        var paren = text.IndexOf('(');
        if (paren >= 0)
            text = text[..paren];

        text = text.Trim(TrimChars);
        foreach (var prefix in PluralPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..].Trim();
        }

        if (text.Length == 0)
            return true;

        foreach (var suffix in HollowSuffixes)
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var head = text[..^suffix.Length].Trim(TrimChars);
            if (MetaHeads.Contains(head))
                return true;
        }

        return MetaHeads.Contains(text);
    }

    /// <summary>
    /// Câu BÁO CÁO TRẠNG THÁI của hệ thống thay vì hỏi người dùng — *"Bảng thông báo theo sự kiện chưa được
    /// chốt."*, *"Danh sách vai trò chưa xác định."* Đây là hình dạng mà cái tên trường CŨ (<c>gap</c> —
    /// "điều còn thiếu") mời gọi: một chỗ hụt được mô tả đúng, nhưng cổng thì phát nguyên văn nó ra màn
    /// hình như một câu hỏi.
    ///
    /// <para>
    /// Nhận diện theo ĐUÔI CÂU, cố ý hẹp: chỉ bắt khi câu KẾT THÚC bằng một vị ngữ trạng thái. Một câu hỏi
    /// thật thường mở đầu bằng cụm đó rồi mới tới nội dung (*"chưa rõ ai duyệt đơn thay trưởng phòng"*,
    /// *"chưa chốt cách tính điểm cuối kỳ"*) nên nó không lọt vào lưới này; còn *"cách tính điểm chưa rõ"*
    /// thì lọt, và đó là ĐÚNG — nó cũng chỉ đang tường thuật, cổng nên rơi về nhánh phát lại.
    /// </para>
    /// </summary>
    public static bool IsStateReport(string question)
    {
        var text = question.ToLowerInvariant().Trim().Trim(TrimChars);
        return StateReportTails.Any(tail => text.EndsWith(tail, StringComparison.Ordinal));
    }

    /// <summary>
    /// Nhóm được chốt bằng BẢNG chứ không bằng câu hỏi — «Phân quyền theo nghiệp vụ» và «Thông báo / nhắc
    /// nhở». <c>requirement-chat.v4.md</c> cấm BA hỏi lẻ hai nhóm này (ai nhận thông báo, quyền theo màn
    /// hình), nên một câu hỏi gắn vào đây KHÔNG có đường nào được trả lời: cổng phát nó ra, người dùng đáp
    /// trong chat, mà lượt distill kế tiếp thì chỉ nhận bằng chứng từ chính hai bảng ấy. Bỏ trống ô này để
    /// hai cổng bảng (<see cref="PermissionMatrixGate"/>, <see cref="NotificationMapGate"/>) làm việc của
    /// chúng, và để cổng readiness rơi về câu phát lại — thứ đóng lại được bằng một lượt.
    ///
    /// <para>
    /// So khớp hai chiều bằng TIỀN TỐ qua <see cref="CoverageMapParser.IsSameGroup"/>: một lượt distill
    /// viết chệch phần đuôi nhãn không được phép làm guard câm.
    /// </para>
    /// </summary>
    public static bool IsTableDecidedGroup(string? label)
        => TableDecidedLabels.Any(known => CoverageMapParser.IsSameGroup(label, known));

    private static readonly string[] TableDecidedLabels =
    {
        PermissionMatrixGate.PermissionGroupLabel, NotificationMapGate.NotificationGroupLabel
    };

    private static readonly char[] TrimChars = { ' ', '.', ',', ';', ':', '-', '–', '…' };

    private static readonly string[] PluralPrefixes = { "các ", "những ", "một số " };

    private static readonly string[] HollowSuffixes = { "khác", "còn lại", "bổ sung", "chưa nêu" };

    // Danh từ chỉ CHỖ của câu trả lời chứ không chở câu trả lời nào — cùng vai trò với
    // BAChatReplyParser.MetaChipHeads, và cũng phải hẹp vì lý do y hệt.
    private static readonly HashSet<string> MetaHeads = new(StringComparer.Ordinal)
    {
        "quy tắc", "quy tắc nghiệp vụ", "quy định", "ràng buộc", "thông tin", "yêu cầu", "nội dung",
        "chi tiết", "điểm", "mục", "phần", "dữ liệu", "vấn đề", "ý"
    };

    // Vị ngữ trạng thái đứng CUỐI câu: dấu hiệu của một câu tường thuật về hệ thống. Hẹp có chủ đích — mỗi
    // mục thêm vào đây là một câu hỏi thật có nguy cơ bị xoá oan. Cái giá của xoá oan vẫn rẻ hơn giữ nhầm
    // (cùng cách cân giá với CoverageStaleGapGuard): xoá oan ⇒ cổng hỏi một câu phát lại đóng được; giữ
    // nhầm ⇒ buổi phỏng vấn không bao giờ kết thúc.
    private static readonly string[] StateReportTails =
    {
        "chưa được chốt", "chưa chốt", "chưa được xác nhận", "chưa xác nhận", "chưa được xác định",
        "chưa xác định", "chưa có thông tin", "chưa được thiết lập", "chưa được cấu hình",
        "chưa hoàn thiện", "chưa đầy đủ", "chưa rõ", "chưa có"
    };
}
