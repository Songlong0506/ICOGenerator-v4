using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Requirements;

/// <summary>Một câu nghiệm thu của Product Brief: tính năng nó thuộc về (có thể rỗng) và chính câu đó.</summary>
public sealed record BriefAcceptanceCriterion(string Feature, string Criterion);

/// <summary>
/// Bóc các dòng <c>"Hoàn thành khi: …"</c> khỏi Product Brief đã duyệt.
///
/// <para>
/// Vì sao cần một parser riêng: <c>Prompts/BusinessAnalyst/product-brief.v3.md</c> bắt MỖI tính năng chính
/// phải có một dòng nghiệm thu ngay bên dưới, viết bằng ngôn ngữ người dùng nghiệp vụ hiểu được — nên đây
/// là TIÊU CHÍ NGHIỆM THU DUY NHẤT mà chính người dùng đọc và đã bấm Approve. Nhưng bước sinh AI Design
/// Spec chỉ nhận Product Brief dưới dạng văn bản tự do và không có mục nào để đổ các câu này vào, còn
/// <see cref="UatScenarioService"/> lại sinh kịch bản nghiệm thu TỪ SPEC — nghĩa là bộ kịch bản người dùng
/// sắp bấm thử được suy diễn lại từ bản kỹ thuật thay vì bám câu họ đã duyệt. Bóc ra ở đây để nối thẳng
/// Brief → spec (§ 14. Acceptance Criteria) → UAT, và để <see cref="SpecBriefParityChecker"/> soát được
/// câu nào rơi rụng dọc đường.
/// </para>
/// </summary>
public static partial class BriefAcceptanceCriteria
{
    // Chặn phòng thủ: một Brief hợp lệ có vài chục tính năng là cùng.
    private const int MaxCriteria = 30;

    /// <summary>
    /// Các câu nghiệm thu theo thứ tự xuất hiện. <c>Feature</c> là bullet cấp 0 gần nhất phía trên (rỗng
    /// khi câu đứng ngay sau một heading). Trùng nội dung bị bỏ; Brief không có dòng nào ⇒ danh sách rỗng.
    /// </summary>
    public static IReadOnlyList<BriefAcceptanceCriterion> Parse(string? productBrief)
    {
        var result = new List<BriefAcceptanceCriterion>();
        if (string.IsNullOrWhiteSpace(productBrief))
            return result;

        var currentFeature = string.Empty;

        foreach (var raw in productBrief.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
                continue;

            // Câu nghiệm thu được nhận diện TRƯỚC bullet: model đôi khi viết nó thành bullet con
            // ("- Hoàn thành khi: …") và ở mức thụt lề 0, lúc đó nó vẫn khớp cả hai mẫu.
            var criterion = CriterionRegex().Match(line);
            if (criterion.Success)
            {
                var text = CleanInline(criterion.Groups[1].Value);
                if (text.Length > 0
                    && result.Count < MaxCriteria
                    && !result.Any(x => PocSpec.Key(x.Criterion) == PocSpec.Key(text)))
                {
                    result.Add(new BriefAcceptanceCriterion(currentFeature, text));
                }
                continue;
            }

            // Heading mới ⇒ quên tính năng đang giữ: một câu nghiệm thu phải thuộc về tính năng ngay
            // trên nó, không phải bullet sót lại từ mục trước (vd mục "Dành cho ai?").
            if (line.TrimStart().StartsWith("##", StringComparison.Ordinal))
            {
                currentFeature = string.Empty;
                continue;
            }

            var bullet = TopLevelBulletRegex().Match(line);
            if (bullet.Success)
                currentFeature = CleanInline(bullet.Groups[1].Value);
        }

        return result;
    }

    /// <summary>
    /// Khối nối vào prompt sinh AI Design Spec: render sẵn ĐÚNG các dòng mà mục "## 14. Acceptance
    /// Criteria" phải chứa, để model chỉ việc chép thay vì tự diễn đạt lại (diễn đạt lại là cách nhanh
    /// nhất để câu nghiệm thu người dùng đã duyệt biến thành một câu khác). Rỗng ⇒ chuỗi rỗng (Brief cũ
    /// chưa có dòng "Hoàn thành khi" thì prompt như trước).
    /// </summary>
    public static string BuildPromptBlock(IReadOnlyList<BriefAcceptanceCriterion> criteria)
    {
        if (criteria.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Câu nghiệm thu người dùng ĐÃ DUYỆT trong Product Brief (các dòng \"Hoàn thành khi: …\" dưới mỗi tính năng chính). Chép NGUYÊN VĂN các dòng dưới đây vào mục \"## 14. Acceptance Criteria\" của spec — giữ đúng mã AC-n, KHÔNG diễn đạt lại, KHÔNG gộp, KHÔNG bỏ bớt câu nào. Đây là điều người dùng sẽ dùng để nghiệm thu bản demo, nên nó là ĐÍCH của POC chứ không phải văn bản tham khảo:");
        sb.Append(Render(criteria));
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Các câu nghiệm thu ở đúng định dạng của mục "## 14. Acceptance Criteria"
    /// (<c>- AC-n (&lt;tính năng&gt;): &lt;câu nghiệm thu&gt;</c>) — dùng cho cả prompt sinh spec lẫn báo
    /// cáo lệch của <see cref="SpecBriefParityChecker"/>, để hai nơi không bao giờ mô tả cùng một thứ
    /// theo hai kiểu khác nhau.
    /// </summary>
    public static string Render(IReadOnlyList<BriefAcceptanceCriterion> criteria)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < criteria.Count; i++)
        {
            var item = criteria[i];
            var feature = string.IsNullOrWhiteSpace(item.Feature) ? string.Empty : $" ({item.Feature})";
            sb.AppendLine($"- AC-{i + 1}{feature}: {item.Criterion}");
        }
        return sb.ToString();
    }

    // Bỏ nhấn mạnh markdown + nháy bao ngoài mà model hay thêm ("*Hoàn thành khi: …*", "\"…\"").
    private static string CleanInline(string text)
    {
        var value = (text ?? string.Empty)
            .Replace("**", string.Empty)
            .Replace("`", string.Empty)
            .Trim();
        value = value.Trim('*').Trim();
        if (value.Length >= 2 && value[0] is '"' or '“' && value[^1] is '"' or '”')
            value = value[1..^1].Trim();
        return value;
    }

    // Dòng nghiệm thu: thụt lề tuỳ ý, có thể là bullet con, có thể bọc trong dấu nhấn mạnh/nháy.
    // Nhóm 1 = phần sau dấu hai chấm.
    [GeneratedRegex("^\\s*(?:[-*+]\\s+)?[\"“*]*\\s*Hoàn thành khi\\s*[::]\\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CriterionRegex();

    // Bullet cấp 0 (không thụt lề) — một tính năng chính của Brief.
    [GeneratedRegex("^[-*+]\\s+(.+)$")]
    private static partial Regex TopLevelBulletRegex();
}
