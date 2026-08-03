using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// So khớp "hai câu này có phải cùng một điều không" cho các cổng đối chiếu chạy trên văn bản do LLM
/// sinh (tiêu đề kịch bản UAT ↔ tên trong <c>pocScenarios()</c>, quy tắc của Product Brief ↔ Business
/// Rule của spec, câu nghiệm thu ↔ mục § 14).
///
/// <para>
/// Vì sao không so bằng nhau: mọi đầu ra đều được yêu cầu "chép nguyên văn", nhưng model thường xuyên
/// thêm/bớt vài từ, đổi dấu câu hoặc viết hoa khác đi. So khớp chặt biến mọi khác biệt vô hại thành một
/// báo cáo lệch giả, và cổng bị bỏ qua vì kêu quá nhiều. Ba mức nới dần: bằng nhau sau chuẩn hoá → chứa
/// nhau (chỉ khi chuỗi ngắn hơn đủ dài để không khớp bừa) → đủ tỷ lệ TỪ CHUNG.
/// </para>
/// </summary>
public static class TextSimilarity
{
    /// <summary>Độ dài tối thiểu để cho phép so khớp kiểu "chứa nhau" — chuỗi quá ngắn dễ khớp nhầm.</summary>
    public const int DefaultMinContainmentLength = 8;

    /// <summary>Tỷ lệ từ chung tối thiểu để coi hai câu là cùng một điều.</summary>
    public const double DefaultMinTokenOverlap = 0.6;

    /// <summary>Hai câu nói về cùng một điều (bằng nhau / chứa nhau / đủ từ chung).</summary>
    public static bool Matches(
        string? a,
        string? b,
        int minContainmentLength = DefaultMinContainmentLength,
        double minTokenOverlap = DefaultMinTokenOverlap)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return false;
        if (left == right)
            return true;

        var shorter = left.Length <= right.Length ? left : right;
        if (shorter.Length >= minContainmentLength
            && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)))
            return true;

        return TokenOverlap(left, right) >= minTokenOverlap;
    }

    /// <summary>
    /// Tỷ lệ từ chung giữa hai chuỗi ĐÃ chuẩn hoá, tính trên mẫu số là bên ÍT từ hơn — một câu spec dài
    /// dòng vẫn khớp được với câu Brief ngắn gọn cùng nội dung. Từ ≤ 2 ký tự bị bỏ (hư từ tiếng Việt/Anh
    /// xuất hiện ở mọi câu nên chỉ làm nhiễu điểm).
    /// </summary>
    public static double TokenOverlap(string normalizedA, string normalizedB)
    {
        var tokensA = Tokens(normalizedA);
        var tokensB = Tokens(normalizedB);
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0;
        var common = tokensA.Count(t => tokensB.Contains(t));
        return (double)common / Math.Min(tokensA.Count, tokensB.Count);
    }

    /// <summary>Bỏ dấu câu + gộp khoảng trắng + thường hoá: hai câu chỉ khác dấu phẩy/nháy vẫn là một.</summary>
    public static string Normalize(string? text)
    {
        var cleaned = PunctuationRegex.Replace((text ?? string.Empty).ToLowerInvariant(), " ");
        return WhitespaceRegex.Replace(cleaned, " ").Trim();
    }

    private static HashSet<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet(StringComparer.Ordinal);

    private static readonly Regex PunctuationRegex = new(@"[^\p{L}\p{N}\s]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
}
