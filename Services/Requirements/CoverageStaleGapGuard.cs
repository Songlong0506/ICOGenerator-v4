using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH chống <b>mẩu <c>còn thiếu:</c> đã chết</b>: một dòng bản đồ không được vừa ghi
/// nhận một điều vừa ghi rằng chính điều đó còn thiếu. Chạy ở đường GHI của bản đồ bao phủ, ngay trước
/// <see cref="CoverageConfirmedTableGuard"/>.
///
/// <para>
/// <b>Vì sao cần một cái phanh riêng.</b> <c>requirement-coverage.v4.md</c> đã ghi luật này ("tóm tắt đã
/// chứa câu trả lời thì mẩu <c>còn thiếu:</c> phải BIẾN MẤT"), nhưng nó là luật cho model — mà lượt
/// distill được đính CHÍNH bản đồ cũ, nên cách rẻ nhất để model xuất ra một dòng "hợp lệ" là chép lại
/// nguyên mẩu cũ. Ca thật (dự án <i>JD Libary 4</i>, buổi 24 lượt): người dùng trả lời điểm đau ở lượt 5,
/// distiller ghi trọn bốn điểm đó vào dòng «Quy trình hiện tại &amp; điểm khó» ở trạng thái <c>[RÕ]</c>,
/// nhưng dòng «Mục tiêu / bài toán» vẫn giữ <i>còn thiếu: Chưa rõ điểm khó chịu nhất khi làm việc bằng 2
/// file Excel là gì</i> suốt 19 lượt sau đó. Cùng buổi, dòng «Quy tắc nghiệp vụ &amp; ràng buộc» ghi đủ ba
/// quy tắc rồi vẫn kèm <i>còn thiếu: Chưa rõ các quy tắc bắt buộc … (ví dụ mã JD duy nhất, Responsibility
/// tổng % bằng 100)</i> — đúng ba quy tắc nó vừa liệt kê.
/// </para>
///
/// <para>
/// Thiệt hại là một <b>vòng lặp kín</b>, không phải một dòng xấu: <see cref="RequirementReadinessGate"/>
/// lấy NGUYÊN mẩu đó làm câu chặn, nên lượt 24 của buổi trên là câu hỏi của lượt 4 phát lại nguyên văn —
/// người dùng trả lời đúng thứ họ đã trả lời, distiller lại chép mẩu cũ sang lượt sau, và nút
/// "Write Requirement" khoá vĩnh viễn. Phanh chống hỏi lại
/// (<see cref="AskedQuestionHistory"/>) không đỡ được ca này: câu chặn do chính cổng dựng ra, không phải
/// câu model sinh.
/// </para>
///
/// <para>
/// <b>Chỉ XOÁ mẩu, KHÔNG BAO GIỜ nâng trạng thái.</b> Bằng chứng ở đây do LLM chắt, nên guard không được
/// phép suy ra "đã đủ" — khác <see cref="CoverageConfirmedTableGuard"/>, nơi bằng chứng là từng ô người
/// dùng tự tay bấm. Dòng mất mẩu vẫn đứng ở <c>[MỘT PHẦN]</c> và cổng rơi về nhánh PHÁT LẠI của
/// <see cref="RequirementReadinessGate"/> (<i>"Mình đang ghi nhận: … Phần này còn chỗ nào chưa đúng hoặc
/// còn thiếu không?"</i>) — một câu hỏi ĐÓNG LẠI ĐƯỢC bằng một lượt, thay cho một câu hỏi không có câu trả
/// lời nào đúng. Vòng lặp bị cắt ở chỗ nó thật sự kín, còn quyền nâng <c>[RÕ]</c> vẫn nằm ở lượt distill
/// kế tiếp.
/// </para>
///
/// <para>
/// <b>Xoá nhầm rẻ hơn giữ nhầm.</b> Xoá nhầm ⇒ cổng hỏi một câu xác nhận và người dùng bấm một chip; giữ
/// nhầm ⇒ buổi phỏng vấn không bao giờ kết thúc. Cùng cách cân giá với các chốt chặn của
/// <see cref="BAChatReplyParser"/>, nên ngưỡng ở đây cố ý nới hơn phanh chống hỏi lại.
/// </para>
/// </summary>
public static partial class CoverageStaleGapGuard
{
    /// <summary>
    /// Tỷ lệ từ NỘI DUNG của mẩu <c>còn thiếu:</c> phải tìm thấy trong phần đã ghi nhận thì mới coi là đã
    /// được trả lời. Đo BAO PHỦ một chiều (mẩu ⊂ phần ghi nhận) chứ không đo tương đồng hai chiều như
    /// <see cref="AskedQuestionHistory"/>: phần ghi nhận luôn dài hơn mẩu hỏi nhiều lần, nên Jaccard ở đây
    /// chỉ đo được độ chênh lệch độ dài.
    ///
    /// <para>
    /// Con số đọc ra từ chính bốn dòng của buổi <i>JD Libary 4</i>: hai mẩu đã chết đo được 0.71 và 0.89,
    /// hai mẩu còn sống ("ai được XOÁ danh mục JD", "JD bị TRÙNG TÊN thì sao" — phần thân dòng thật sự
    /// không trả lời) đo được 0.46 và 0.40. Ngưỡng đặt vào giữa khoảng trống đó và cố ý lệch xuống phía
    /// xoá, đúng theo cách cân giá ở phần tóm tắt của lớp này.
    /// </para>
    /// </summary>
    private const double AnsweredContainment = 0.65;

    /// <summary>
    /// Mẩu ngắn hơn thế này thì KHÔNG xét — vài từ nội dung thì cái gì cũng nằm lọt trong một tóm tắt dài,
    /// và một mẩu ngắn ("chốt lại các bước của luồng chính") thường là mẩu rộng cố ý, không phải mẩu chết.
    /// </summary>
    private const int MinGapWords = 5;

    /// <summary>
    /// Xoá mọi mẩu <c>còn thiếu:</c> mà chính bản đồ đã trả lời — bằng phần đã ghi nhận của CHÍNH dòng đó,
    /// hoặc bằng phần đã ghi nhận của một dòng <c>[RÕ]</c> khác. Trạng thái và bằng chứng của mọi dòng giữ
    /// nguyên. Không có mẩu nào chết ⇒ trả về đúng chuỗi đã nhận.
    /// </summary>
    public static string? Apply(string? coverageMap)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return coverageMap;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return coverageMap;

        // Kho lời giải cho một mẩu ở dòng KHÁC: chỉ phần đã ghi nhận của các dòng [RÕ]. Dòng [MỘT PHẦN]
        // không được làm chứng cho dòng khác — nó tự nó còn đang thiếu, và hai dòng cùng dở dang xác nhận
        // lẫn nhau là cách nhanh nhất để guard xoá một mẩu còn sống.
        var clearBodies = items
            .Where(x => "RÕ".Equals(x.Status, StringComparison.Ordinal))
            .Select(x => Words(x.Known))
            .ToList();

        var changed = false;
        foreach (var item in items)
        {
            if (item.Gap.Length == 0)
                continue;

            // Cụm tín hiệu tái mở (người dùng vừa đính chính nhóm này) đứng ngoài mọi phép xoá: nó không
            // phải một câu hỏi mà là một lệnh MỞ LẠI nhóm — xoá nó là bịt đúng đường người dùng vừa mở.
            if (item.Gap.Contains(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase))
                continue;

            var gapWords = Words(item.Gap);
            if (gapWords.Count < MinGapWords)
                continue;

            var answeredHere = Covers(Words(item.Known), gapWords);
            if (!answeredHere && !clearBodies.Any(body => Covers(body, gapWords)))
                continue;

            // Chỉ mẩu còn thiếu bị xoá: trạng thái, phần đã ghi nhận và bằng chứng của dòng giữ nguyên.
            item.Gap = string.Empty;
            changed = true;
        }

        return changed ? CoverageMapParser.Serialize(items) : coverageMap;
    }

    /// <summary>
    /// Lọc danh sách "Điểm cần làm rõ còn tồn đọng" trước khi <see cref="CoveragePendingGuard"/> ghi chúng
    /// vào bản đồ: mục nào bản đồ đã trả lời thì bỏ đi. Không có bộ lọc này thì guard trên chỉ dọn được
    /// mẩu do distiller viết, còn cùng mẩu ấy quay lại ngay ở lượt sau qua đường tồn đọng — danh sách tồn
    /// đọng chắt ở HẬU KỲ nên nó luôn cũ hơn bản đồ đúng một lượt (xem <see cref="CoveragePendingGuard"/>).
    /// </summary>
    public static IReadOnlyList<string> DropAnsweredItems(string? coverageMap, IReadOnlyList<string> openQuestions)
    {
        if (string.IsNullOrWhiteSpace(coverageMap) || openQuestions.Count == 0)
            return openQuestions;

        var clearBodies = CoverageMapParser.Parse(coverageMap)
            .Where(x => "RÕ".Equals(x.Status, StringComparison.Ordinal))
            .Select(x => Words(x.Summary))
            .ToList();

        if (clearBodies.Count == 0)
            return openQuestions;

        var kept = openQuestions
            .Where(item =>
            {
                var words = Words(CoveragePendingGuard.StripGroupTag(item));
                return words.Count < MinGapWords || !clearBodies.Any(body => Covers(body, words));
            })
            .ToList();

        return kept.Count == openQuestions.Count ? openQuestions : kept;
    }

    /// <summary>Phần đã ghi nhận có phủ được mẩu còn thiếu không — bao phủ một chiều trên tập từ nội dung.</summary>
    private static bool Covers(IReadOnlyCollection<string> body, IReadOnlyCollection<string> gap)
    {
        if (body.Count == 0 || gap.Count == 0)
            return false;

        var shared = gap.Count(body.Contains);
        return (double)shared / gap.Count >= AnsweredContainment;
    }

    /// <summary>
    /// Tập từ NỘI DUNG của một đoạn: chuẩn hoá bằng <see cref="AskedQuestionHistory.Key"/> (bỏ dấu câu, gộp
    /// khoảng trắng, hạ chữ thường) rồi bỏ hư từ. Không bỏ hư từ thì mọi mẩu tiếng Việt đều trùng mọi tóm
    /// tắt tiếng Việt ở phân nửa số từ, và ngưỡng bao phủ mất hết ý nghĩa.
    /// </summary>
    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in AskedQuestionHistory.Key(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!StopWords.Contains(word))
                words.Add(word);
        }
        return words;
    }

    // Hư từ tiếng Việt + từ vựng khuôn của một mẩu "còn thiếu" ("chưa rõ …", "cần chốt …"). Cố ý HẸP: chỉ
    // các từ không mang nội dung nghiệp vụ nào. Một danh từ nghiệp vụ lọt vào đây là guard xoá quá tay.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "và", "hoặc", "hay", "của", "cho", "với", "về", "trong", "ngoài", "trên", "dưới", "ở", "từ", "đến",
        "là", "có", "không", "chưa", "đã", "sẽ", "được", "bị", "phải", "cần", "còn", "thiếu", "rõ", "thì",
        "mà", "nếu", "khi", "lúc", "này", "đó", "kia", "các", "những", "một", "mỗi", "cả", "nào", "gì",
        "ai", "sao", "đâu", "bao", "nhiêu", "chỉ", "cũng", "vẫn", "rồi", "nữa", "lại", "ra", "vào", "theo",
        "như", "ví", "dụ", "tức", "nên", "vì", "do", "để", "bằng", "sau", "trước", "giữa", "chốt", "làm",
        "việc", "muốn", "hơn", "rất", "đang", "người", "dùng", "hệ", "thống"
    };
}
