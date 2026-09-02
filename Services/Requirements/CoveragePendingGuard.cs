using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH cuối cùng của bản đồ bao phủ: một nhóm KHÔNG được đứng ở <c>[RÕ]</c> trong khi
/// "Điểm cần làm rõ còn tồn đọng" (<see cref="InterviewOutlookService"/>) vẫn còn một mục gắn đúng nhóm
/// đó. Chạy sau lượt distill, trước khi bản đồ được lưu.
///
/// <para>
/// <b>Vì sao cần một cái phanh riêng.</b> Hai danh sách này được chắt bởi HAI lời gọi LLM khác nhau, đọc
/// cùng một hội thoại nhưng không bao giờ nhìn thấy nhau — nên chúng nói ngược nhau mà không tầng nào
/// biết. Ca thật (dự án Learning and Development 7): bản đồ ghi «Luồng ngoại lệ & trường hợp đặc biệt»,
/// «Vòng đời &amp; trạng thái» và «Dữ liệu / danh mục chính» là <c>[RÕ]</c>, trong khi chính hệ thống đang
/// giữ bảy điểm tồn đọng thuộc đúng ba nhóm ấy — *"chưa rõ nhân viên có đăng ký lại được sau khi ticket bị
/// reject không"*, *"chưa rõ kết quả Complete/Not Complete/No Show dùng để chuyển bước nào"*, *"chưa rõ xử
/// lý khi Item ID và Item Title không tạo thành cặp duy nhất"*. Thiệt hại không dừng ở một dòng sai trạng
/// thái: <c>[RÕ]</c> là lệnh CẤM BA hỏi lại nhóm đó (<c>requirement-chat.v4.md</c>), nên bảy điểm ấy vĩnh
/// viễn không bao giờ được lấy, và bước soạn tài liệu — vốn bị cấm giả định — nhận một khoảng trống mà
/// không cổng nào báo.
/// </para>
///
/// <para>
/// <b>Một chiều, không bao giờ nâng cấp.</b> Guard chỉ hạ <c>[RÕ]</c> xuống <c>[MỘT PHẦN]</c>. Hạ nhầm thì
/// BA hỏi thêm một câu và người dùng trả lời lần nữa; bỏ sót thì sinh ra một khoảng trống mà mọi tầng sau
/// tin là đã đủ — hai cái giá không cùng hạng. Cùng luật với các chốt chặn của
/// <see cref="BAChatReplyParser"/>.
/// </para>
///
/// <para>
/// <b>Nhóm của mục tồn đọng là một TRƯỜNG, không phải một thẻ gõ tay.</b> Trước đây guard này nhận danh
/// sách chuỗi và tự regex bóc khuôn <c>[Nhãn] câu hỏi</c> ra — model gõ chệch khuôn là guard câm trong im
/// lặng, đúng cái hỏng mà nó sinh ra để chặn. Nay nhóm đã là <see cref="OpenQuestionEntry.Group"/>, được
/// <c>InterviewOutlookService</c> chốt về đúng một trong 12 nhãn checklist ngay ở đường ghi; xem
/// <see cref="OpenQuestionDocument"/>.
/// </para>
///
/// <para>
/// <b>Chạy ở đường GHI, không ở đường đọc.</b> Bản đồ là "nguồn chân lý duy nhất" mà cổng readiness, panel
/// tiến độ và bốn cổng bảng cùng đọc (<see cref="RequirementReadinessGate"/>,
/// <see cref="InterviewTableGate"/>). Lọc lúc đọc ở MỘT chỗ là dựng lại đúng cảnh hai giám khảo lệch nhau
/// mà thiết kế này đã bỏ đi — nên bản đã hạ cấp là bản được LƯU, và mọi consumer thấy cùng một sự thật.
/// </para>
///
/// <para>
/// <b>Dòng VỪA ĐỔI trong chính lượt này thì đứng ngoài.</b> Danh sách tồn đọng chắt ở hậu kỳ nên nó KHÔNG
/// bao giờ nhìn thấy lượt user mới nhất; còn bản đồ thì vừa gộp đúng lượt đó xong. Vì vậy một mục tồn đọng
/// gắn vào dòng mà lượt distill này vừa viết lại là mục CŨ theo thứ tự thời gian, không phải một khoảng
/// trống còn thật — và ghi nó thành mẩu <c>còn thiếu:</c> là biến câu người dùng vừa trả lời thành câu chặn
/// của cổng. Ca thật (dự án JD Libary 5, lượt 3→4): người dùng kể xong quy trình Excel hiện tại ở lượt 3;
/// lượt 4 nhận lại đúng *"Chưa rõ quy trình hiện tại tạo và gán JD cho nhân viên diễn ra như thế nào (các
/// bước, vai trò tham gia)"* — mục tồn đọng chắt từ lượt 2 — và người dùng dán lại nguyên văn câu vừa gõ.
/// Ba lượt bị đốt. Phép so ở đây là so THÂN DÒNG với bản đồ TRƯỚC distill: đổi ⇒ dòng đã ăn thông tin mới
/// trong lượt này ⇒ bỏ qua mục tồn đọng của nó; không đổi ⇒ mục tồn đọng vẫn còn nguyên giá trị.
/// So bằng nội dung chứ không bằng dấu thời gian vì bản đồ không mang dấu thời gian nào, và distiller được
/// đính chính bản đồ cũ nên một dòng KHÔNG có gì mới sẽ được chép lại y nguyên từng chữ.
/// <paramref name="previousMap"/> bỏ trống ⇒ giữ nguyên hành vi cũ (áp cho mọi dòng).
/// </para>
///
/// <para>
/// <b>Trễ một lượt, và đó là đánh đổi có chủ ý.</b> Bản đồ được gộp NGAY trong lượt chat, còn danh sách
/// tồn đọng chắt ở HẬU KỲ (sau frame done) — nên guard của lượt N đọc danh sách tính tới lượt N−1. Người
/// dùng vừa trả lời đúng mục tồn đọng ở lượt N thì dòng đó vẫn bị hạ một lượt, rồi tự lên lại ở lượt N+1
/// khi lượt chắt lọc bỏ mục đã chốt. Cái giá đó đã có lưới đỡ sẵn: prompt chat bắt BA "tin HỘI THOẠI khi
/// bản đồ chưa kịp cập nhật", và <see cref="AskedQuestionHistory"/> loại thẳng câu hỏi trùng trước khi nó
/// lên màn hình. Chiều ngược lại — chờ cho hai danh sách cùng nhịp — thì phải dời lượt distill xuống hậu
/// kỳ, tức bản đồ dẫn lượt hỏi kế tiếp luôn cũ một lượt, đắt hơn nhiều.
/// </para>
/// </summary>
public static class CoveragePendingGuard
{
    /// <summary>Trần độ dài mẩu "còn thiếu" ghép vào dòng — bản đồ là la bàn, không phải biên bản.</summary>
    private const int MaxQuestionChars = 200;

    /// <summary>
    /// Hạ cấp các dòng <c>[RÕ]</c> còn mục tồn đọng gắn đúng nhóm đó, và ghi mẩu còn phải hỏi vào phần
    /// <c>còn thiếu:</c> — đúng chỗ mà <see cref="RequirementReadinessGate"/> lấy làm câu hỏi hiển thị,
    /// nên điểm tồn đọng thật sự trở thành câu chặn của cổng thay vì một ghi chú không ai đọc.
    /// Không mục nào có nhóm ⇒ trả nguyên bản đồ.
    /// </summary>
    public static string? Apply(string? coverageMap, IReadOnlyList<OpenQuestionEntry> openQuestions, string? previousMap = null)
    {
        if (string.IsNullOrWhiteSpace(coverageMap) || openQuestions.Count == 0)
            return coverageMap;

        // Mục ĐẦU TIÊN của mỗi nhóm là mẩu sẽ được hỏi: BA chỉ hỏi 1–2 câu mỗi lượt, nên dội cả cụm vào
        // một dòng chỉ làm câu chặn của cổng thành một danh sách không trả lời được. Các mục còn lại vẫn
        // nằm nguyên trong khối "Điểm cần làm rõ còn tồn đọng" của ngữ cảnh chat.
        var gaps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in openQuestions)
        {
            var group = item.Group.Trim();
            var text = item.Text.Trim();
            // Mục không gắn được nhóm nào (model viết một tên lạ, đã bị đường ghi xoá về rỗng) ⇒ bỏ qua:
            // guard fail-open, nó không được phép hạ nhầm một dòng vì một cái nhãn vô nghĩa.
            if (group.Length == 0 || text.Length == 0)
                continue;

            gaps.TryAdd(group, text);
        }

        if (gaps.Count == 0)
            return coverageMap;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return coverageMap;

        var previousBodies = ReadBodies(previousMap);
        var changed = false;
        foreach (var item in items)
        {
            if (!"RÕ".Equals(item.Status, StringComparison.Ordinal))
                continue;

            // Dòng vừa đổi nội dung trong chính lượt distill này ⇒ mục tồn đọng gắn vào nó đã cũ hơn dòng.
            // Xem phần "Dòng VỪA ĐỔI" ở doc của class.
            if (ChangedThisTurn(previousBodies, item.Label, item.Summary))
                continue;

            var gap = FindGap(gaps, item.Label);
            if (gap == null)
                continue;

            Downgrade(item, gap);
            changed = true;
        }

        // Không hạ dòng nào ⇒ trả về ĐÚNG chuỗi đã nhận. Serialize lại một bản đồ y hệt là ghi DB thừa ở
        // mọi lượt chat (xem RepairMapAsync, nó chỉ lưu khi chuỗi đổi).
        return changed ? CoverageMapParser.Serialize(items) : coverageMap;
    }

    /// <summary>
    /// Thân dòng (phần tóm tắt, không kể khối <c>{nguồn: …}</c>) của từng nhãn trong một bản đồ. Bản đồ
    /// rỗng/không đọc được ⇒ từ điển rỗng, và mọi dòng được coi là "không đổi" — đúng hành vi cũ.
    /// </summary>
    private static Dictionary<string, string> ReadBodies(string? map)
    {
        var bodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in CoverageMapParser.Parse(map))
            bodies[item.Label] = AskedQuestionHistory.Key(item.Summary);
        return bodies;
    }

    /// <summary>
    /// Dòng này có vừa ăn thông tin mới trong lượt distill vừa rồi không: tóm tắt khác với tóm tắt cùng
    /// nhãn ở bản đồ TRƯỚC đó. Không có bản đồ trước (lượt đầu tiên, hoặc caller không truyền) ⇒ false, giữ
    /// nguyên hành vi cũ. Dòng MỚI xuất hiện lần này cũng tính là vừa đổi — nó vừa được trả lời xong.
    /// So trên khoá đã chuẩn hoá (<see cref="AskedQuestionHistory.Key"/>) để một dấu chấm hay một chữ hoa
    /// đổi chỗ không bị đọc thành "có thông tin mới".
    /// </summary>
    private static bool ChangedThisTurn(Dictionary<string, string> previousBodies, string label, string summary)
    {
        if (previousBodies.Count == 0)
            return false;

        return !previousBodies.TryGetValue(label, out var before)
            || !string.Equals(before, AskedQuestionHistory.Key(summary), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nhóm của mẩu tồn đọng khớp với nhãn dòng bản đồ. So khớp hai chiều bằng TIỀN TỐ, cùng lý do với
    /// <see cref="InterviewTableGate.IsClear"/>: lượt chắt lọc viết *"Luồng ngoại lệ"* còn bản đồ ghi
    /// *"Luồng ngoại lệ &amp; trường hợp đặc biệt"* thì đó vẫn là một nhóm, và một phép so nguyên văn sẽ
    /// làm guard câm trong im lặng. Phía tồn đọng nay đã được chốt về nhãn checklist ở đường ghi, nhưng
    /// phép so vẫn giữ hai chiều vì phía BÊN KIA thì không: nhãn dòng bản đồ do lượt distill chép ra và
    /// vẫn lệch được. Không khớp nhãn nào ⇒ bỏ qua, guard fail-open.
    /// </summary>
    private static string? FindGap(Dictionary<string, string> gaps, string label)
    {
        foreach (var (group, gap) in gaps)
        {
            if (label.StartsWith(group, StringComparison.OrdinalIgnoreCase)
                || group.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return gap;
        }
        return null;
    }

    // Hạ dòng xuống [MỘT PHẦN] và ghi mẩu còn phải hỏi vào trường Gap — đúng chỗ RequirementReadinessGate
    // lấy làm câu chặn. Bằng chứng của dòng giữ nguyên: nó là căn cứ cho phần ĐÃ ghi nhận, không phải cho
    // phần còn thiếu, và xoá nó đi là làm panel tiến độ mất lý do vì sao nhóm này từng được chấm [RÕ].
    private static void Downgrade(CoverageMapItem item, string gap)
    {
        var body = item.Summary.Trim();
        if (body.Length > 0 && !body.EndsWith('.') && !body.EndsWith(';'))
            body += ".";

        item.Status = "MỘT PHẦN";
        item.Known = body;
        item.NextQuestion = gap.Length > MaxQuestionChars ? gap[..MaxQuestionChars].TrimEnd() : gap;
    }
}
