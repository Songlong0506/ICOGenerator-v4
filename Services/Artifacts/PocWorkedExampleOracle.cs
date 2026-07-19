using System.Globalization;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Oracle ĐỘC LẬP cho business logic của POC: đối chiếu KỲ VỌNG lấy từ "## 13. Worked Examples" của AI
/// Design Spec (con số do NGƯỜI DÙNG chốt trong lúc phỏng vấn) với GIÁ TRỊ do chính POC tính ra qua
/// window.pocWorkedExamples(). Đây là điểm khác biệt so với window.pocSelfTest(): self-test do agent viết
/// cả logic lẫn kỳ vọng (tự chấm mình); ở đây kỳ vọng nằm NGOÀI tầm agent (đến từ spec/hội thoại), agent
/// chỉ cung cấp con số POC tính — sai công thức là lộ ra ngay vì không khớp con số đã chốt.
/// </summary>
public static partial class PocWorkedExampleOracle
{
    /// <summary>
    /// Trả về danh sách ISSUE (rỗng nếu khớp hết): mỗi worked example của spec thiếu trong POC, hoặc POC
    /// tính ra khác kỳ vọng, là một issue. Không có worked example nào ⇒ danh sách rỗng (không đổi hành vi).
    /// </summary>
    public static IReadOnlyList<string> Compare(
        IReadOnlyList<PocWorkedExample> specExamples,
        IReadOnlyList<PocWorkedExampleResult> pocResults)
    {
        var issues = new List<string>();
        if (specExamples.Count == 0)
            return issues;

        foreach (var we in specExamples)
        {
            var computed = pocResults.FirstOrDefault(r => string.Equals(r.Ref, we.Ref, StringComparison.OrdinalIgnoreCase));
            var ruleTag = string.IsNullOrWhiteSpace(we.RuleRef) ? "" : $" ({we.RuleRef})";

            if (computed == null)
            {
                issues.Add($"Worked example {we.Ref}{ruleTag} chưa được POC tính — thêm mục {{ ref:'{we.Ref}', computed:<giá trị POC tính từ '{we.Description}'> }} vào window.pocWorkedExamples() để đối chiếu với kỳ vọng '{we.Expected}'.");
                continue;
            }

            if (!ValuesMatch(we.Expected, computed.Computed))
                issues.Add($"Worked example {we.Ref}{ruleTag} SAI: người dùng đã chốt kỳ vọng '{we.Expected}' cho '{we.Description}', nhưng POC tính ra '{computed.Computed}'. Sửa LOGIC nghiệp vụ trong SetPocScript cho ra đúng kỳ vọng — TUYỆT ĐỐI không đổi kỳ vọng cho khớp.");
        }

        return issues;
    }

    // So khớp kỳ vọng vs giá trị POC tính: ưu tiên so SỐ (bóc con số đầu tiên ở mỗi bên, so với sai số nhỏ)
    // để "81", "81 điểm", "81.0" coi như bằng nhau; không bóc được số thì so chuỗi đã chuẩn hoá (bỏ khoảng
    // trắng/hoa-thường, quy dấu phẩy nghìn) hoặc chứa nhau.
    public static bool ValuesMatch(string expected, string computed)
    {
        var e = expected.Trim();
        var c = computed.Trim();

        var en = ExtractNumber(e);
        var cn = ExtractNumber(c);
        if (en.HasValue && cn.HasValue)
            return Math.Abs(en.Value - cn.Value) <= 0.01 + 1e-9 * Math.Max(Math.Abs(en.Value), Math.Abs(cn.Value));

        var ek = Normalize(e);
        var ck = Normalize(c);
        if (ek.Length == 0 || ck.Length == 0)
            return ek == ck;
        return ek == ck || ek.Contains(ck, StringComparison.Ordinal) || ck.Contains(ek, StringComparison.Ordinal);
    }

    // Bóc con số đầu tiên: bỏ dấu phẩy/khoảng trắng ngăn cách nghìn ("1,234" / "1 234" → 1234), nhận cả số
    // âm và thập phân dấu chấm.
    private static double? ExtractNumber(string text)
    {
        var cleaned = ThousandsRegex().Replace(text, "$1$2");
        var m = NumberRegex().Match(cleaned);
        if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(text.Trim(), "").ToLowerInvariant().TrimEnd('.', ',', ';');

    [GeneratedRegex(@"(\d)[,\s](\d{3})")]
    private static partial Regex ThousandsRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
