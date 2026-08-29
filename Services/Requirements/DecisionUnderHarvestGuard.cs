using System.Text;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH chống <b>nhật ký BỎ SÓT</b>: một lô lượt mà bản đồ bao phủ đã trích được bằng chứng
/// TỪ CHÍNH lời người dùng trong lô đó thì không thể là một lô "không có quyết định nào". Chạy ở đường GHI
/// của <see cref="DecisionLogService"/>, ngay sau lượt chắt lọc và trước khi dời con trỏ.
///
/// <para>
/// <b>Vì sao cần một cái phanh riêng.</b> <c>decision-log.v1.md</c> đã có luật này ("Phép thử chống BỎ
/// SÓT"), nhưng nó bắt model tự chấm chính mình — lưới yếu nhất trong mọi lưới của repo này, và nó đã
/// rách. Ca thật ghi ngay trong prompt (dự án <i>JD Libary 5</i>): sau 26 lượt, trong đó người dùng đã
/// chốt vai trò nào gán JD, bộ trường của một JD, bộ trường của một lần gán, việc bỏ ngày hết hạn, việc
/// không cần báo cáo và quy mô sử dụng, <b>nhật ký chỉ có ĐÚNG MỘT dòng</b>.
/// </para>
///
/// <para>
/// Thiệt hại không nằm ở nhật ký mà ở tầng sau: <see cref="RequirementConflictService"/> soát mâu thuẫn
/// BẰNG CHÍNH danh sách này, nên một nhật ký gần rỗng làm cả cơ chế soát mâu thuẫn <b>mù</b> — mâu thuẫn
/// của buổi đó ("không cần báo cáo" chọi với chính điểm đau "khó biết JD nào đang gán cho ai") đi thẳng
/// vào tài liệu. <see cref="ProductBriefDraftService"/> cũng đọc nhật ký như tập điều đã duyệt, và nhật ký
/// không còn mặt UI nào để người dùng tự rà.
/// </para>
///
/// <para>
/// <b>Bộ đọc thứ hai là thứ phát hiện ra.</b> <see cref="RequirementCoverageService"/> đọc ĐÚNG các lượt
/// đó bằng một lời gọi khác, và mọi dòng <c>[RÕ]</c>/<c>[MỘT PHẦN]</c> của bản đồ đều phải kèm khối
/// <c>{nguồn: …}</c> trích ngắn lời người dùng. Vậy nên: một trích dẫn của bản đồ nằm trong lời người dùng
/// của lô này ⇒ lô này CÓ nội dung nghiệp vụ, đã được một bộ đọc độc lập xác nhận. Nhật ký không dài thêm
/// và không sửa dòng nào trong khi đó ⇒ nghi bỏ sót. Đây là lý do hai bộ chắt phải ở TÁCH nhau: gộp làm
/// một thì không còn ai đối chiếu với ai.
/// </para>
///
/// <para>
/// <b>Chỉ NGHI, không sửa.</b> Guard không bịa được dòng nhật ký còn thiếu nên nó không viết gì — nó chỉ
/// trả về cờ nghi ngờ kèm đúng các trích dẫn đã khớp, để caller chắt lại MỘT lần với chúng làm chỉ dẫn
/// (xem <see cref="DecisionLogService"/>). Nghi nhầm ⇒ tốn một lời gọi ở đường HẬU KỲ, người dùng không
/// chờ thêm giây nào; bỏ sót thật ⇒ soát mâu thuẫn mù cho tới hết dự án. Hai cái giá không cùng hạng.
/// </para>
/// </summary>
public static partial class DecisionUnderHarvestGuard
{
    // Trích dẫn ngắn hơn chừng này không đủ để kết luận "câu này lấy từ lô lượt đang xét": các mẩu như
    // "có" / "tất cả" trùng nhau ở mọi buổi phỏng vấn. Ngưỡng tính trên chuỗi ĐÃ chuẩn hoá.
    private const int MinEvidenceChars = 12;

    /// <summary>Kết quả phép thử: có nghi bỏ sót không, và các trích dẫn của bản đồ đã khớp với lô lượt.</summary>
    /// <param name="SuspectsMiss">Lô có bằng chứng nhưng nhật ký không đổi ⇒ nghi bỏ sót.</param>
    /// <param name="Evidence">Các trích dẫn <c>{nguồn: …}</c> tìm thấy trong lời người dùng của lô (nguyên văn như bản đồ ghi).</param>
    public readonly record struct Result(bool SuspectsMiss, IReadOnlyList<string> Evidence);

    /// <summary>
    /// Đối chiếu một lô đã chắt: bản đồ bao phủ hiện hành, lời người dùng trong lô, nhật ký TRƯỚC và SAU
    /// lượt chắt. Bản đồ rỗng / không trích được gì / nhật ký có đổi ⇒ không nghi ngờ gì.
    /// </summary>
    public static Result Check(string? coverageMap, IEnumerable<string> userTurnTexts, string? logBefore, string? logAfter)
    {
        var evidence = EvidenceFoundIn(coverageMap, userTurnTexts);
        return new Result(evidence.Count > 0 && Unchanged(logBefore, logAfter), evidence);
    }

    /// <summary>
    /// Các trích dẫn <c>{nguồn: …}</c> của bản đồ mà lời người dùng trong lô CHỨA. Trích dẫn có thể là một
    /// câu nói được đóng ngoặc bên trong khối, hoặc chính cả khối (bản đồ cũ, hoặc trích không ngoặc); mô
    /// tả kiểu <i>"bảng phân quyền người dùng đã chốt"</i> đơn giản là không khớp với lượt nào — im lặng,
    /// đúng chiều an toàn.
    /// </summary>
    public static IReadOnlyList<string> EvidenceFoundIn(string? coverageMap, IEnumerable<string> userTurnTexts)
    {
        var haystacks = userTurnTexts
            .Select(Normalize)
            .Where(x => x.Length >= MinEvidenceChars)
            .ToList();
        if (haystacks.Count == 0)
            return Array.Empty<string>();

        var found = new List<string>();
        foreach (var item in CoverageMapParser.Parse(coverageMap))
        {
            if (string.IsNullOrWhiteSpace(item.Evidence))
                continue;

            foreach (var candidate in Candidates(item.Evidence))
            {
                var needle = Normalize(candidate);
                if (needle.Length < MinEvidenceChars)
                    continue;
                if (!haystacks.Any(h => h.Contains(needle, StringComparison.Ordinal)))
                    continue;

                if (!found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    found.Add(candidate);
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Hai nhật ký có cùng tập dòng hay không (so trên dòng đã tách + chuẩn hoá, nên đổi thứ tự hay đổi
    /// khoảng trắng KHÔNG tính là đổi — model chắt lại y hệt vẫn phải bị coi là "không đổi").
    /// </summary>
    public static bool Unchanged(string? logBefore, string? logAfter)
    {
        var before = DecisionLogService.ParseItems(logBefore).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var after = DecisionLogService.ParseItems(logAfter).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return before.SetEquals(after);
    }

    // Một khối {nguồn: …} thường là: "câu người dùng nói" (có ngoặc) hoặc cả khối là câu trích. Lấy các
    // đoạn trong ngoặc trước — chúng mới là lời người dùng, phần còn lại hay là lời dẫn của model
    // ("người dùng nói", "theo tài liệu X") và không bao giờ khớp lượt nào.
    private static IEnumerable<string> Candidates(string evidence)
    {
        var quoted = QuotedRegex().Matches(evidence)
            .Select(m => m.Groups["q"].Value.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        foreach (var q in quoted)
            yield return q;

        yield return evidence.Trim();
    }

    // Chuẩn hoá để so khớp: bỏ dấu ngoặc/chấm câu, gộp mọi khoảng trắng về một dấu cách, hạ chữ thường.
    // KHÔNG bỏ dấu tiếng Việt — hai câu khác dấu là hai câu khác nghĩa, và cả hai vế đều do máy sinh ra từ
    // cùng một nguồn nên chúng giữ nguyên dấu.
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            // Chấm câu và ngoặc bị bỏ hẳn (không thành dấu cách): model trích "đơn khoá luôn," còn lượt
            // gốc ghi "đơn khoá luôn" — khác đúng một dấu phẩy thì vẫn phải khớp.
        }

        return sb.ToString().TrimEnd();
    }

    // Đoạn nằm trong ngoặc kép thẳng, ngoặc kép cong, hoặc ngoặc đơn cong — ba kiểu model hay dùng.
    [GeneratedRegex("[\"“‘'](?<q>[^\"”’']{2,})[\"”’']")]
    private static partial Regex QuotedRegex();
}
