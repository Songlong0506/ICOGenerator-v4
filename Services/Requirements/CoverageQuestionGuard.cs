using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn cho CHẤT LƯỢNG của danh sách câu hỏi (<see cref="OpenQuestionDocument"/>): cuộc phỏng vấn
/// không được mang một "câu hỏi" mà người dùng đọc lên không biết phải trả lời gì, cũng không được giữ một
/// câu hỏi mà chính bản đồ đã trả lời. Chạy ở đường GHI, đầu chuỗi guard — nó dọn danh sách câu hỏi cho các
/// guard đứng sau.
///
/// <para>
/// <b>Vì sao phải là một cái phanh chứ không chỉ là luật trong prompt.</b>
/// <c>requirement-coverage.v5.md</c> đã ghi cả hai luật ("câu hỏi phải hỏi được một điều cụ thể", "tóm tắt
/// đã chứa câu trả lời thì câu hỏi phải BIẾN MẤT"), nhưng lượt distill được đính CHÍNH danh sách cũ, nên
/// cách rẻ nhất để model xuất ra một danh sách "hợp lệ" là chép lại nguyên câu cũ. Câu hỏi hỏng nằm trong
/// DB thì đi vào ngữ cảnh CHAT ở mọi lượt sau qua <see cref="BAChatPromptBlocks"/>, hiện trên tooltip của
/// panel "Tiến độ khai thác", và được <see cref="RequirementReadinessGate"/> phát NGUYÊN VĂN làm câu chặn.
/// Lọc ở đường ghi thì mọi tầng thấy CÙNG một sự thật — đúng lý do <see cref="CoveragePendingGuard"/> chọn
/// chạy ở đường ghi.
/// </para>
///
/// <para>
/// <b>Hai ca thật.</b> (1) Dự án <i>JD Libary 4</i>, buổi 24 lượt: người dùng trả lời điểm đau ở lượt 5,
/// distiller ghi trọn bốn điểm đó vào dòng «Quy trình hiện tại &amp; điểm khó» ở trạng thái <c>[RÕ]</c>,
/// nhưng dòng «Mục tiêu / bài toán» vẫn giữ câu hỏi <i>Chưa rõ điểm khó chịu nhất khi làm việc bằng 2 file
/// Excel là gì</i> suốt 19 lượt sau đó — cổng lấy NGUYÊN câu đó làm câu chặn, người dùng trả lời đúng thứ
/// họ đã trả lời, distiller lại chép mục cũ sang lượt sau, và nút "Write Requirement" khoá vĩnh viễn. Một
/// <b>vòng lặp kín</b>, không phải một dòng xấu. (2) Dự án quản lý khóa học bắt buộc: dòng «Thông báo /
/// nhắc nhở» đứng <c>[MỘT PHẦN]</c> với câu hỏi <i>"Bảng thông báo theo sự kiện chưa được chốt."</i> — một
/// câu MÔ TẢ TRẠNG THÁI HỆ THỐNG, không hỏi ai điều gì, mà cổng vẫn đọc nguyên văn ra màn hình.
/// </para>
///
/// <para>
/// <b>Hai nguồn loại câu hỏi</b> (chỉ xoá câu hỏi, xem luật một chiều bên dưới):
/// <list type="number">
///   <item>HÌNH DẠNG, tất định, làm ngay tại đây — câu rỗng sau khi lược ghi chú máy, và câu gắn vào HAI
///         NHÓM CHỐT BẰNG BẢNG («Phân quyền theo nghiệp vụ», «Thông báo / nhắc nhở»): BA bị cấm hỏi lẻ
///         chúng, nên mọi câu hỏi gắn vào hai dòng ấy đều là câu hỏi CHẾT dù viết hay tới đâu
///         (<see cref="IsTableDecidedGroup"/>).</item>
///   <item>NGHĨA, do <see cref="InterviewJudge.JudgeOpenQuestionsAsync"/> phán — câu bản đồ ĐÃ TRẢ LỜI,
///         câu RỖNG NGHĨA (*"các quy tắc khác (nếu có)"*), câu TƯỜNG THUẬT TRẠNG THÁI (*"…chưa được
///         chốt."*). Caller gọi trọng tài rồi đưa các mục bị loại vào <see cref="Apply"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// ĐỪNG TÌM <c>CoverageStaleGapGuard</c>, <c>IsHollow</c> hay <c>IsStateReport</c>: ba phép thử đó trả lời
/// câu hỏi về NGHĨA bằng mặt chữ tiếng Việt — tỷ lệ từ trùng sau khi bỏ một bảng hư từ, một bảng danh từ
/// mê-ta gắn đuôi "khác / còn lại / bổ sung", một bảng vị ngữ trạng thái đứng cuối câu. Ngưỡng và bảng đều
/// đo trên vài buổi tiếng Việt cụ thể, và không bảng nào khớp nổi một chữ khi hội thoại bằng ngôn ngữ khác.
/// Chúng đã được thay bằng lượt trọng tài.
/// </para>
///
/// <para>
/// <b>Một chiều: chỉ XOÁ câu hỏi, KHÔNG BAO GIỜ đụng trạng thái</b> — và cũng không đánh dấu câu hỏi là
/// <c>ĐÃ TRẢ LỜI</c>: bằng chứng ở đây do LLM phán, nên guard không được phép ký tên người dùng vào một câu
/// trả lời, khác <see cref="CoverageConfirmedTableGuard"/> nơi bằng chứng là từng ô người dùng tự tay bấm.
/// Xoá là phép sửa NHẸ NHẤT đóng được vòng lặp: danh sách câu hỏi được lượt chắt lọc kế tiếp viết lại trọn
/// vẹn nên một câu bị xoá oan vẫn quay lại được. Dòng mất câu hỏi vẫn đứng <c>[MỘT PHẦN]</c> và cổng rơi về
/// nhánh PHÁT LẠI (*"Mình đang ghi nhận: … Phần này còn chỗ nào chưa đúng hoặc còn thiếu không?"*) — một
/// câu ĐÓNG LẠI ĐƯỢC bằng một lượt. Quyền nâng <c>[RÕ]</c> vẫn nằm ở lượt distill kế tiếp, quyền nâng hai
/// dòng chốt-bằng-bảng vẫn nằm ở <see cref="CoverageConfirmedTableGuard"/>.
/// </para>
///
/// <para>
/// <b>Xoá nhầm rẻ hơn giữ nhầm.</b> Xoá nhầm ⇒ cổng hỏi một câu xác nhận và người dùng bấm một chip; giữ
/// nhầm ⇒ buổi phỏng vấn không bao giờ kết thúc. Trọng tài lỗi thì không xoá gì (fail-open) — đúng hành vi
/// của danh sách trước khi có lớp này.
/// </para>
///
/// <para>
/// <b>Dòng mang cụm <see cref="AskedQuestionHistory.ReopenNote"/> đứng NGOÀI mọi luật.</b> Đó là người
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
    /// Xoá khỏi <paramref name="questions"/> mọi câu hỏi MỞ không dùng được: hỏng về hình dạng
    /// (<see cref="IsUsable"/>), hoặc nằm trong <paramref name="rejected"/> — các mục lượt trọng tài vừa
    /// loại (so theo THAM CHIẾU, nên caller phải đưa đúng các phần tử của danh sách này). Không có phán
    /// quyết (đường sửa chữa không gọi model, hoặc trọng tài lỗi) ⇒ chỉ phần hình dạng chạy.
    /// </summary>
    public static void Apply(IList<OpenQuestionEntry> questions, IReadOnlyCollection<OpenQuestionEntry>? rejected = null)
    {
        // Duyệt NGƯỢC để xoá tại chỗ mà không nhảy cóc phần tử.
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            var question = questions[i];
            if (!question.IsOpen || IsReopened(question))
                continue;

            if (IsUsable(question.Text, question.Group) && rejected?.Contains(question) != true)
                continue;

            questions.RemoveAt(i);
        }
    }

    /// <summary>
    /// Mục này có đáng đem cho trọng tài xét không: còn MỞ, không mang cụm tái mở, và qua được phần hình
    /// dạng. Cùng ba điều kiện mà <see cref="Apply"/> dùng để quyết định có được phép xoá — đưa cho trọng
    /// tài một câu mà đằng nào cũng không được xoá (hoặc đằng nào cũng bị xoá) là tốn token cho không.
    /// </summary>
    public static bool IsJudgeable(OpenQuestionEntry question)
        => question.IsOpen && !IsReopened(question) && IsUsable(question.Text, question.Group);

    // Người dùng vừa đính chính nhóm này ⇒ để nguyên cả mục: phần sau cụm tín hiệu là câu hỏi họ vừa mở
    // ra, và bản thân cụm tín hiệu còn được đọc để miễn phanh chống-hỏi-lại.
    private static bool IsReopened(OpenQuestionEntry question)
        => question.Text.Contains(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Câu hỏi này bày ra màn hình được không, xét riêng về HÌNH DẠNG — <c>false</c> nghĩa là phải xoá. Mở
    /// cho <see cref="RequirementReadinessGate"/> dùng lại ở đường đọc: cổng vẫn phải tự vệ với thứ đang
    /// nằm sẵn trong DB. Phần NGHĨA (rỗng nghĩa, tường thuật trạng thái, đã được trả lời) không có ở đây —
    /// nó cần một lời gọi model, nên chỉ chạy ở đường ghi.
    /// </summary>
    public static bool IsUsable(string? question, string? group)
        => StripMachineNotes(question).Length > 0 && !IsTableDecidedGroup(group);

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
}
