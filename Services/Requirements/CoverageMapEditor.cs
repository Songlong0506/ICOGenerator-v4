using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sửa TẤT ĐỊNH một dòng của "Bản đồ bao phủ yêu cầu" — đường duy nhất để NGƯỜI DÙNG đính chính bản đồ.
///
/// <para>
/// Bản đồ do LLM chấm và là nguồn chân lý duy nhất của cổng "Write Requirement". Khi nó chấm một nhóm
/// [RÕ] oan, hệ thống bước vào một điểm mù kín: BA được lệnh KHÔNG hỏi lại nhóm đã [RÕ], nên nhóm đó
/// không bao giờ được nhắc tới nữa và cách hiểu sai đi thẳng vào tài liệu. Nút "chưa đúng" cạnh mỗi
/// dòng mở lại nhóm bằng một phép sửa chuỗi tại chỗ (không gọi LLM, không chờ lượt chat kế tiếp) — người
/// dùng thấy thanh tiến độ lùi lại ngay và biết chắc mình đã được nghe.
/// </para>
/// </summary>
public static partial class CoverageMapEditor
{
    /// <summary>
    /// Hạ dòng có nhãn <paramref name="label"/> xuống [MỘT PHẦN] kèm ghi chú "người dùng báo chưa đúng".
    /// Trả về <c>null</c> khi không tìm thấy dòng nào khớp (bản đồ rỗng, nhãn lạ) để caller báo lỗi thay
    /// vì ghi đè bản đồ bằng một bản không đổi.
    /// </summary>
    public static string? Reopen(string? coverageMap, string label)
    {
        if (string.IsNullOrWhiteSpace(coverageMap) || string.IsNullOrWhiteSpace(label))
            return null;

        var lines = coverageMap.Replace("\r\n", "\n").Split('\n');
        var target = Normalize(label);
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var match = CoverageLineRegex().Match(lines[i].Trim());
            if (!match.Success || Normalize(match.Groups["label"].Value) != target)
                continue;

            var core = match.Groups["core"].Success ? "★ " : string.Empty;
            var previous = match.Groups["summary"].Value.Trim();
            // Giữ lại tóm tắt cũ trong ngoặc: lượt gộp sau (LLM) đọc được BA đã hiểu gì và người dùng phủ
            // nhận điều gì, thay vì mất trắng ngữ cảnh và hỏi lại từ số không.
            var note = "còn thiếu: người dùng báo phần này chưa đúng — cần hỏi lại và chốt lại.";
            if (previous.Length > 0)
                note += $" (ghi nhận trước đó: {previous})";

            lines[i] = $"- {core}{match.Groups["label"].Value.Trim()}: [MỘT PHẦN] {note}";
            changed = true;
            break;
        }

        return changed ? string.Join("\n", lines) : null;
    }

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace((text ?? string.Empty).Trim(), " ").ToLowerInvariant();

    // Cùng dạng dòng mà CoverageMapParser đọc — hai bên phải hiểu bản đồ y hệt nhau.
    [GeneratedRegex(@"^-\s*(?<core>★)?\s*(?<label>[^:\[\]]+):\s*\[(?<status>[^\]]+)\]\s*(?<summary>.*)$")]
    private static partial Regex CoverageLineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
