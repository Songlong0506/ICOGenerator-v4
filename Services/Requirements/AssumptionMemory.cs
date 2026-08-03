using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// TRÍ NHỚ "giả định người dùng ĐÃ duyệt đúng" (<c>Project.ConfirmedAssumptions</c>) — cặp đối xứng của
/// <c>Project.SpecAssumptionCorrections</c> (những giả định họ đã BÁC).
///
/// Lý do tồn tại: mỗi lần user bấm "chưa đúng" ở cổng xác nhận giả định, hệ thống sinh LẠI AI Design Spec,
/// và spec mới lại đẻ ra mục "## 12. Assumptions" gần như y hệt — nên cổng dựng lại hỏi NGUYÊN VĂN cả
/// những điểm user vừa bấm "Đúng" ở vòng trước. Nhìn từ phía người dùng thì đó là "tôi trả lời rồi mà BA
/// cứ hỏi mãi". Nhớ lại phần đã duyệt cho phép hai việc:
///  - cổng chỉ HỎI phần thật sự mới (phần cũ vẫn hiện nhưng gấp lại, đánh dấu sẵn "Đúng" — user đổi ý
///    được, chỉ là không bị bắt trả lời lại);
///  - lượt sinh lại mà KHÔNG đẻ ra giả định mới nào thì tự xác nhận, chạy thẳng sang dựng POC.
///
/// So khớp bằng khoá chuẩn hoá (bỏ hoa/thường, gộp khoảng trắng, bỏ dấu câu cuối) chứ không so nguyên
/// văn: LLM sinh lại hay đổi "Mỗi nhân viên chỉ thuộc một phòng ban." ↔ "Mỗi nhân viên chỉ thuộc một
/// phòng ban" — đổi một dấu chấm mà bắt user duyệt lại thì trí nhớ này thành vô dụng. Ngược lại, câu bị
/// viết lại thật sự (khác chữ) CỐ Ý không khớp: đó là giả định mới, phải hỏi.
/// </summary>
public static partial class AssumptionMemory
{
    // Trần độ dài cột: đây là bộ nhớ "đã duyệt", không phải nhật ký. Chạm trần thì bỏ dòng CŨ nhất —
    // giả định cũ nhất là thứ ít khả năng còn xuất hiện trong spec hiện tại nhất.
    private const int MaxChars = 6000;

    /// <summary>Tách cột lưu (mỗi dòng một giả định) thành danh sách; null/rỗng ⇒ rỗng.</summary>
    public static IReadOnlyList<string> Split(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Array.Empty<string>();

        return stored.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    /// <summary>Khoá so khớp — xem ghi chú lớp về việc vì sao không so nguyên văn.</summary>
    public static string Key(string? assumption)
    {
        if (string.IsNullOrWhiteSpace(assumption))
            return string.Empty;

        var text = WhitespaceRegex().Replace(assumption.Replace("**", string.Empty), " ").Trim();
        return TrailingPunctuationRegex().Replace(text, string.Empty).ToLowerInvariant();
    }

    /// <summary>Tập khoá của các giả định đã được duyệt đúng.</summary>
    public static HashSet<string> KeySet(string? stored) =>
        Split(stored).Select(Key).Where(k => k.Length > 0).ToHashSet();

    /// <summary>
    /// Các giả định của spec mà user CHƯA từng duyệt — tức phần cổng thật sự cần hỏi. Rỗng ⇒ không có gì
    /// mới, khỏi dựng cổng.
    /// </summary>
    public static IReadOnlyList<string> SelectUnconfirmed(IEnumerable<string>? assumptions, string? stored)
    {
        if (assumptions == null)
            return Array.Empty<string>();

        var confirmed = KeySet(stored);
        return assumptions.Where(a => !confirmed.Contains(Key(a))).ToList();
    }

    /// <summary>
    /// Ghi nhận thêm các giả định vừa được duyệt đúng, đồng thời QUÊN những giả định vừa bị bác
    /// (<paramref name="rejected"/>) — user đổi ý thì trí nhớ phải đổi theo, nếu không một giả định đã bác
    /// vẫn bị coi là "đã duyệt" ở lượt sinh sau và không bao giờ được hỏi lại.
    /// </summary>
    public static string? Remember(string? stored, IEnumerable<string>? confirmed, IEnumerable<string>? rejected = null)
    {
        var rejectedKeys = (rejected ?? Array.Empty<string>()).Select(Key).Where(k => k.Length > 0).ToHashSet();

        var kept = new List<string>();
        var seen = new HashSet<string>();
        foreach (var item in Split(stored).Concat(confirmed ?? Array.Empty<string>()))
        {
            var text = WhitespaceRegex().Replace(item ?? string.Empty, " ").Trim();
            var key = Key(text);
            if (key.Length == 0 || rejectedKeys.Contains(key) || !seen.Add(key))
                continue;
            kept.Add(text);
        }

        // Cắt từ ĐẦU (cũ nhất) theo từng dòng trọn vẹn — cắt giữa dòng sẽ đẻ ra một khoá rác không khớp
        // với bất cứ giả định nào.
        while (kept.Count > 0 && kept.Sum(x => x.Length + 1) > MaxChars)
            kept.RemoveAt(0);

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\s.;:,！!。]+$")]
    private static partial Regex TrailingPunctuationRegex();
}
