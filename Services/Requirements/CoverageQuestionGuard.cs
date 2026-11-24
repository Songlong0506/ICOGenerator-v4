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
///   <item>HÌNH DẠNG, tất định, làm ngay tại đây — câu rỗng sau khi lược ghi chú máy, và câu gắn vào NHÓM
///         KHÔNG-HỎI (<see cref="CoverageGroupKey.Unasked"/> — «Thông báo / nhắc nhở»): BA bị cấm hỏi lẻ nó, nên mọi câu hỏi gắn vào dòng ấy đều là câu
///         hỏi CHẾT dù viết hay tới đâu (<see cref="IsUnaskedGroup"/>).</item>
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
/// trả lời, khác <see cref="CoverageUnaskedGroupGuard"/> nơi "bằng chứng" là chính một quyết định thiết kế.
/// Xoá là phép sửa NHẸ NHẤT đóng được vòng lặp: danh sách câu hỏi được lượt chắt lọc kế tiếp viết lại trọn
/// vẹn nên một câu bị xoá oan vẫn quay lại được. Dòng mất câu hỏi vẫn đứng <c>[MỘT PHẦN]</c> và cổng rơi về
/// nhánh PHÁT LẠI (*"Mình đang ghi nhận: … Phần này còn chỗ nào chưa đúng hoặc còn thiếu không?"*) — một
/// câu ĐÓNG LẠI ĐƯỢC bằng một lượt. Quyền nâng <c>[RÕ]</c> vẫn nằm ở lượt distill kế tiếp, quyền nâng dòng
/// không-hỏi vẫn nằm ở <see cref="CoverageUnaskedGroupGuard"/>.
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
/// dùng vừa nói BA hiểu sai nhóm này — đúng một lần họ chủ động mở lại đường hỏi, kể cả với nhóm phân
/// quyền (nhóm không-hỏi thì không có đường hỏi nào để mở lại: <see cref="CoverageUnaskedGroupGuard"/>
/// chạy ngay sau vẫn xoá câu của nó). Và cụm ấy còn là TÍN HIỆU MÁY: <see cref="AskedQuestionHistory.ReopenedGroups"/> đọc nó
/// (qua <see cref="CoverageMapItem.Summary"/>) để miễn phanh chống-hỏi-lại cho nhóm đó, nên xoá ô này là
/// cướp mất cái đường ấy trong im lặng.
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

    /// <summary>
    /// Câu hỏi này đã nằm trong danh sách của lượt TRƯỚC chưa (so phần hỏi thật, đã lược ghi chú máy, bỏ
    /// khác biệt khoảng trắng và hoa/thường; trạng thái cũ là gì cũng tính). <c>false</c> nghĩa là distiller
    /// vừa viết MỚI câu này ở lượt này.
    ///
    /// <para>
    /// <b>Vì sao câu MỚI không được xoá với lý do <c>answered</c>.</b> Lý do ấy sinh ra cho vòng lặp câu hỏi
    /// CHẾT: distiller được đính danh sách cũ nên chép lại một câu mà bản đồ đã trả lời từ lâu (ca <i>JD
    /// Libary 4</i>). Một câu vừa được viết mới thì distiller viết nó trong khi nhìn ĐÚNG bản đồ mà trọng tài
    /// đang nhìn — nó gần như luôn là một chỗ hở thật vừa lộ ra. Ca thật (dự án quản lý locker): người dùng
    /// xin thêm màn hình quản lý Tòa nhà/Khu vực, distiller viết hai câu mới đúng chỗ (ai được thêm/sửa/xoá,
    /// xoá khi còn tủ thì sao), trọng tài xoá cả hai, và cổng rơi về nhánh phát lại — người dùng nghe đọc
    /// lại đúng điều họ vừa nói rồi bị hỏi "còn chỗ nào chưa đúng không?".
    /// </para>
    ///
    /// <para>
    /// So NGUYÊN VĂN là cố ý: đường rẻ nhất của distiller là chép lại nguyên câu cũ, nên câu chết lượt sau
    /// đã thành câu "cũ" và trọng tài lại được quyền xoá nó. Distiller diễn đạt lại câu cũ thì câu ấy được
    /// coi là mới thêm đúng một lượt — cái giá là người dùng bị hỏi câu đó một lần.
    /// </para>
    /// </summary>
    public static bool IsCarriedOver(OpenQuestionEntry question, IReadOnlyCollection<OpenQuestionEntry> previous)
    {
        var text = Comparable(StripMachineNotes(question.Text));
        return text.Length > 0 && previous.Any(p => Comparable(StripMachineNotes(p.Text)) == text);
    }

    /// <summary>
    /// Phán quyết <c>answered</c> có BÁM vào bản đồ không: <paramref name="evidence"/> không rỗng, và từng
    /// mẩu (đã chuẩn hoá khoảng trắng, hoa/thường, dấu câu cuối) nằm trong một mẩu đã ghi nhận của dòng
    /// ĐƯỢC PHÉP làm bằng chứng — dòng của chính nhóm câu hỏi, hoặc một dòng <c>[RÕ]</c>.
    ///
    /// <para>
    /// Đây là luật nguồn bằng chứng của <c>coverage-question-judge.v1.md</c> ("phần đã ghi nhận của một nhóm
    /// khác đang <c>[MỘT PHẦN]</c> KHÔNG được dùng làm bằng chứng"), nay được code thi hành thay vì chỉ tin
    /// model nhớ. Không đo được "câu trả lời có TRỌN không" — đó vẫn là việc của model — nhưng bắt trọng tài
    /// chỉ tay vào một dòng cụ thể thì một phán quyết đoán mò (không trích được gì, trích một câu không có
    /// trong bản đồ, trích từ nhóm không được phép) không xoá được câu hỏi nào. Lệch thì GIỮ câu hỏi: xoá
    /// nhầm một câu hỏi mới đắt hơn hỏi thêm một lượt.
    /// </para>
    /// </summary>
    public static bool IsGroundedAnswer(
        OpenQuestionEntry question, IReadOnlyList<CoverageMapItem> items, IReadOnlyCollection<string>? evidence)
    {
        var quotes = (evidence ?? Array.Empty<string>()).Select(Comparable).Where(q => q.Length > 0).ToList();
        if (quotes.Count == 0)
            return false;

        var groupKey = CoverageGroups.KeyOf(question.Group);
        var allowed = items
            .Where(i => i.Status == CoverageStatus.Clear || (groupKey != null && i.Key == groupKey))
            .SelectMany(i => i.Known)
            .Select(Comparable)
            .Where(k => k.Length > 0)
            .ToList();

        return quotes.All(q => allowed.Any(k => k.Contains(q, StringComparison.Ordinal)));
    }

    // Dạng so khớp của một câu: gộp khoảng trắng, chữ thường, bỏ ký hiệu đầu dòng của khối bản đồ ("·", "-")
    // và dấu câu ở hai đầu — model chép lại một dòng hay kèm hoặc rơi mất mấy ký tự đó.
    private static string Comparable(string? text)
    {
        var oneLine = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Trim('·', '-', '*', ' ', '.', ';', ',', '"', '“', '”').ToLowerInvariant();
    }

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
        => StripMachineNotes(question).Length > 0 && !IsUnaskedGroup(group);

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
    /// Nhóm KHÔNG đi đường câu hỏi (<see cref="CoverageGroupKey.Unasked"/> — «Thông báo / nhắc nhở», không
    /// chốt ở buổi phỏng vấn, chỉ ghi nhận điều người dùng tự kể). Ngữ cảnh chat cấm BA hỏi lẻ nhóm này, nên
    /// một câu hỏi gắn vào đây KHÔNG có đường nào được trả lời. Bỏ trống ô này để
    /// <see cref="CoverageUnaskedGroupGuard"/> đặt dòng về trạng thái không chặn, và để cổng readiness rơi về câu phát lại — thứ đóng lại được bằng
    /// một lượt.
    ///
    /// <para>
    /// Nhận ra nhóm qua <see cref="CoverageGroups.KeyOf"/>: chuỗi nhóm của một câu hỏi là do model viết,
    /// nên nó được kéo về KHOÁ (khớp nhãn ở mọi ngôn ngữ, hai chiều bằng tiền tố) trước khi so.
    /// </para>
    /// </summary>
    public static bool IsUnaskedGroup(string? label)
        => CoverageGroupKey.IsUnasked(CoverageGroups.KeyOf(label));
}
