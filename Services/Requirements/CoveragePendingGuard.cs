using System.Text.RegularExpressions;

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
/// <b>Chạy ở đường GHI, không ở đường đọc.</b> Bản đồ là "nguồn chân lý duy nhất" mà cổng readiness, panel
/// tiến độ và bốn cổng bảng cùng đọc (<see cref="RequirementReadinessGate"/>,
/// <see cref="InterviewTableGate"/>). Lọc lúc đọc ở MỘT chỗ là dựng lại đúng cảnh hai giám khảo lệch nhau
/// mà thiết kế này đã bỏ đi — nên bản đã hạ cấp là bản được LƯU, và mọi consumer thấy cùng một sự thật.
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
public static partial class CoveragePendingGuard
{
    /// <summary>Trần độ dài mẩu "còn thiếu" ghép vào dòng — bản đồ là la bàn, không phải biên bản.</summary>
    private const int MaxGapChars = 200;

    /// <summary>
    /// Hạ cấp các dòng <c>[RÕ]</c> còn mục tồn đọng gắn đúng nhóm đó, và ghi mẩu còn phải hỏi vào phần
    /// <c>còn thiếu:</c> — đúng chỗ mà <see cref="RequirementReadinessGate"/> lấy làm câu hỏi hiển thị,
    /// nên điểm tồn đọng thật sự trở thành câu chặn của cổng thay vì một ghi chú không ai đọc.
    /// Không có mục nào gắn thẻ ⇒ trả nguyên bản đồ.
    /// </summary>
    public static string? Apply(string? coverageMap, IReadOnlyList<string> openQuestions)
    {
        if (string.IsNullOrWhiteSpace(coverageMap) || openQuestions.Count == 0)
            return coverageMap;

        // Mục ĐẦU TIÊN của mỗi nhóm là mẩu sẽ được hỏi: BA chỉ hỏi 1–2 câu mỗi lượt, nên dội cả cụm vào
        // một dòng chỉ làm câu chặn của cổng thành một danh sách không trả lời được. Các mục còn lại vẫn
        // nằm nguyên trong khối "Điểm cần làm rõ còn tồn đọng" của ngữ cảnh chat.
        var gaps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in openQuestions)
        {
            var match = TaggedItemRegex().Match(item.Trim());
            if (!match.Success)
                continue;

            var group = match.Groups["group"].Value.Trim();
            var text = match.Groups["text"].Value.Trim();
            if (group.Length == 0 || text.Length == 0)
                continue;

            gaps.TryAdd(group, text);
        }

        if (gaps.Count == 0)
            return coverageMap;

        var lines = coverageMap.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = CoverageMapParser.LineRegex().Match(lines[i].Trim());
            if (!match.Success)
                continue;

            var label = match.Groups["label"].Value.Trim();
            var status = match.Groups["status"].Value.Trim();
            if (!"RÕ".Equals(status, StringComparison.OrdinalIgnoreCase))
                continue;

            var gap = FindGap(gaps, label);
            if (gap == null)
                continue;

            lines[i] = Downgrade(match.Groups["core"].Success, label, match.Groups["summary"].Value.Trim(), gap);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Bỏ thẻ nhóm ở đầu một mục tồn đọng. Thẻ là từ vựng NỘI BỘ của bản đồ (*«Vòng đời &amp; trạng thái»*)
    /// — nó tồn tại cho guard, không phải cho BA đọc ra: prompt chat cấm ném tên nhóm vào mặt người dùng
    /// nghiệp vụ, và một danh sách gắn thẻ nạp thẳng vào ngữ cảnh là mời BA chép lại đúng thứ đó.
    /// Mục không gắn thẻ ⇒ trả nguyên.
    /// </summary>
    public static string StripGroupTag(string item)
    {
        var match = TaggedItemRegex().Match((item ?? string.Empty).Trim());
        return match.Success ? match.Groups["text"].Value.Trim() : (item ?? string.Empty).Trim();
    }

    /// <summary>
    /// Nhóm của mẩu tồn đọng khớp với nhãn dòng bản đồ. So khớp hai chiều bằng TIỀN TỐ, cùng lý do với
    /// <see cref="InterviewTableGate.IsClear"/>: lượt chắt lọc viết *"Luồng ngoại lệ"* còn bản đồ ghi
    /// *"Luồng ngoại lệ &amp; trường hợp đặc biệt"* thì đó vẫn là một nhóm, và một phép so nguyên văn sẽ
    /// làm guard câm trong im lặng. Thẻ không khớp nhãn nào (model tự nghĩ ra một tên) ⇒ bỏ qua: guard
    /// fail-open, nó không được phép hạ nhầm một dòng vì một cái thẻ vô nghĩa.
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

    // Dựng lại dòng ở [MỘT PHẦN], giữ nguyên ★, nhãn, tóm tắt và khối {nguồn: …}. Khối bằng chứng phải ở
    // CUỐI (CoverageMapParser.SplitEvidence chỉ nhận nó ở đó), nên mẩu "còn thiếu" chèn vào TRƯỚC nó —
    // ghép ẩu ra sau là mất luôn phần bằng chứng của dòng khỏi panel tiến độ.
    private static string Downgrade(bool isCore, string label, string summary, string gap)
    {
        var (body, evidence) = CoverageMapParser.SplitEvidence(summary);
        body = body.Trim();
        if (body.Length > 0 && !body.EndsWith('.') && !body.EndsWith(';'))
            body += ".";

        var trimmedGap = gap.Length > MaxGapChars ? gap[..MaxGapChars].TrimEnd() : gap;
        var rebuilt = (body + " còn thiếu: " + trimmedGap).Trim();
        if (evidence.Length > 0)
            rebuilt += " {nguồn: " + evidence + "}";

        return "- " + (isCore ? "★ " : string.Empty) + label + ": [MỘT PHẦN] " + rebuilt;
    }

    // "[Vòng đời & trạng thái] Chưa rõ kết quả Complete dùng để chuyển bước nào" — thẻ nhóm do
    // interview-outlook.v1.md gắn ở ĐẦU mỗi mục tồn đọng.
    [GeneratedRegex(@"^\[(?<group>[^\]]{1,80})\]\s*(?<text>.+)$", RegexOptions.Singleline)]
    private static partial Regex TaggedItemRegex();
}
