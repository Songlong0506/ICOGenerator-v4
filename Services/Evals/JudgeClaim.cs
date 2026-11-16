using System.Text.Json;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Một KHẲNG ĐỊNH của phán quyết trọng tài, dạng chuẩn hoá để so tập hợp: <c>("repeats", "Q2")</c> = "câu Q2 là
/// hỏi lại", <c>("emptyChips", "Q1 · Khác")</c> = "chip «Khác» của Q1 là chip rỗng", <c>("asksForFile", "có")</c>.
/// Trường boolean chỉ sinh khẳng định khi nó là <c>true</c> — <c>false</c> là "không phán gì", đúng hiệu lực
/// của nó ở các chốt chặn.
/// </summary>
public sealed record JudgeClaim(string Field, string Key)
{
    /// <summary>Khoá của một trường boolean đang bật.</summary>
    public const string True = "có";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string ToString() => $"{Field}: {Key}";

    public static string Serialize(IEnumerable<JudgeClaim> claims) =>
        JsonSerializer.Serialize(claims.OrderBy(c => c.Field, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal), Json);

    /// <summary>Đọc lại danh sách đã lưu; null/hỏng ⇒ null (lượt đó không chấm được).</summary>
    public static IReadOnlyList<JudgeClaim>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<JudgeClaim>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hai phán quyết có cùng hiệu lực (cùng tập khẳng định) hay không.</summary>
    public static bool SameSet(IEnumerable<JudgeClaim> a, IEnumerable<JudgeClaim> b) =>
        a.ToHashSet().SetEquals(b);
}
