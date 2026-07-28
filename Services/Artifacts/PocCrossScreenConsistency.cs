using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Đối chiếu dữ liệu mẫu GIỮA CÁC MÀN HÌNH của POC: cùng một bản ghi (cùng khoá ở cột đầu) hiện ở hai màn
/// khác nhau thì các cột CÙNG TÊN phải mang cùng giá trị.
///
/// <para>
/// Lớp lỗi này lọt qua mọi cổng hiện có vì cổng nào cũng xét từng màn RIÊNG LẺ: màn "Danh sách đơn" ghi
/// đơn của Trần Văn Hùng là 3 ngày phép, màn "Duyệt đơn" ghi 5 ngày — mỗi màn tự nó đều hợp lệ, self-test
/// của agent cũng pass vì nó chỉ gọi hàm tính chứ không so hai bảng. Nhưng người xem demo bắt được ngay
/// trong mười giây đầu, và mất niềm tin vào cả bản demo.
/// </para>
///
/// <para>
/// Nguyên tắc để KHÔNG báo động giả: chỉ so những khoá <b>duy nhất trong từng bảng</b> (khoá lặp lại trong
/// cùng một bảng thì đó là dữ liệu giao dịch — một khách hàng có nhiều đơn — chứ không phải định danh của
/// bản ghi), chỉ so các cột trùng tên, và bỏ qua ô rỗng.
/// </para>
/// </summary>
public static class PocCrossScreenConsistency
{
    /// <summary>Trần số phát hiện báo cho agent — đủ để sửa, không biến report thành danh sách dài vô tận.</summary>
    private const int MaxIssues = 5;

    /// <summary>Khoá quá ngắn ("1", "A") không phải định danh bản ghi — so sẽ chỉ sinh nhiễu.</summary>
    private const int MinKeyLength = 3;

    public static IReadOnlyList<string> Check(IReadOnlyList<PocScreenTable> tables)
    {
        if (tables == null || tables.Count < 2)
            return Array.Empty<string>();

        // (khoá bản ghi, tên cột) → danh sách (màn hình, giá trị hiển thị). Chỉ nạp từ các bảng mà khoá
        // thật sự là định danh (xuất hiện đúng một lần trong bảng đó). Khoá/cột được chuẩn hoá để so, còn
        // bản NGUYÊN VĂN được giữ riêng — thông báo phải đọc được như người dùng thấy trên màn hình.
        var index = new Dictionary<(string Key, string Column), List<(string Screen, string Value)>>();
        var display = new Dictionary<(string Key, string Column), (string Key, string Column)>();

        foreach (var table in tables)
        {
            if (table.Headers.Count < 2 || table.Rows.Count == 0)
                continue;

            var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
            {
                var key = NormalizeKey(row.Count > 0 ? row[0] : string.Empty);
                if (key.Length >= MinKeyLength)
                    keyCounts[key] = keyCounts.GetValueOrDefault(key) + 1;
            }

            foreach (var row in table.Rows)
            {
                var key = NormalizeKey(row.Count > 0 ? row[0] : string.Empty);
                if (key.Length < MinKeyLength || keyCounts.GetValueOrDefault(key) != 1)
                    continue;

                for (var col = 1; col < row.Count && col < table.Headers.Count; col++)
                {
                    var column = NormalizeKey(table.Headers[col]);
                    var value = (row[col] ?? string.Empty).Trim();
                    // Cột hành động (nút Sửa/Xoá) và ô rỗng không mang dữ liệu để so.
                    if (column.Length == 0 || value.Length == 0 || IsActionColumn(column))
                        continue;

                    if (!index.TryGetValue((key, column), out var list))
                    {
                        index[(key, column)] = list = new List<(string, string)>();
                        display[(key, column)] = ((row[0] ?? string.Empty).Trim(), (table.Headers[col] ?? string.Empty).Trim());
                    }
                    list.Add((table.Screen, value));
                }
            }
        }

        var issues = new List<string>();
        foreach (var ((key, column), occurrences) in index)
        {
            if (issues.Count >= MaxIssues)
                break;

            // Chỉ xét khi bản ghi xuất hiện ở TỪ HAI MÀN HÌNH trở lên: hai dòng cùng màn đã bị loại ở trên.
            var screens = occurrences.Select(o => o.Screen).Distinct(StringComparer.Ordinal).ToList();
            if (screens.Count < 2)
                continue;

            var distinctValues = occurrences
                .GroupBy(o => NormalizeValue(o.Value), StringComparer.Ordinal)
                .ToList();
            if (distinctValues.Count < 2)
                continue;

            var detail = string.Join(" ≠ ", occurrences
                .GroupBy(o => o.Screen, StringComparer.Ordinal)
                .Select(g => $"màn '{g.Key}': {g.First().Value}"));

            var (keyText, columnText) = display.TryGetValue((key, column), out var d) ? d : (key, column);
            issues.Add(
                $"Dữ liệu mẫu KHÔNG NHẤT QUÁN giữa các màn hình — bản ghi '{keyText}', cột '{columnText}': {detail}. "
                + "Cùng một bản ghi phải hiển thị cùng giá trị ở mọi màn (seed từ MỘT nguồn dữ liệu trong script rồi render lại, "
                + "đừng gõ tay số liệu riêng cho từng màn) — người xem demo đối chiếu hai màn là thấy ngay.");
        }

        return issues;
    }

    private static bool IsActionColumn(string column) =>
        column is "thao tác" or "hành động" or "actions" or "action" or "" ;

    private static string NormalizeKey(string text) =>
        WhitespaceRegex.Replace((text ?? string.Empty).Trim(), " ").ToLowerInvariant();

    // So sánh giá trị bỏ qua khác biệt hình thức: "1.000.000 đ" và "1,000,000đ" là một; "Đã duyệt" và
    // "đã duyệt " là một. Chỉ khác biệt THẬT mới bị báo.
    private static string NormalizeValue(string text)
    {
        var cleaned = WhitespaceRegex.Replace((text ?? string.Empty).Trim().ToLowerInvariant(), " ");
        return SeparatorRegex.Replace(cleaned, string.Empty);
    }

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SeparatorRegex = new(@"[.,\s]", RegexOptions.Compiled);
}
