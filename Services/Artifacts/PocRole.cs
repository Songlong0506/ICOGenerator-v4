using System.Text.Json;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Danh sách VAI của bản demo — nguồn của khối VIEW AS ở cuối sidebar POC (xem
/// <see cref="PocTemplate.ReplaceRoles"/>). Vai lấy từ "§ 6b. Permission Matrix" của AI Design Spec và
/// do agent gửi kèm <c>SetPocContent</c>; mục menu / section khai báo <c>data-roles</c> theo đúng các
/// nhãn ở đây. POC không có backend nên KHÔNG dựng màn đăng nhập giả: người xem demo đổi vai tại chỗ.
/// </summary>
public static class PocRole
{
    /// <summary>Trần số vai: một bản demo nhiều hơn thế là danh sách người dùng, không phải danh sách vai.</summary>
    public const int MaxRoles = 8;

    private const int MaxLabelLength = 40;

    /// <summary>
    /// Đọc tham số 'roles' của agent: mảng chuỗi, mảng object { "label" }, hoặc một chuỗi
    /// "Manager, HR" (kể cả khi cả mảng bị bọc thành chuỗi JSON — model hay double-encode). Mục hỏng
    /// bị bỏ qua chứ không ném, giữ đúng tinh thần tolerant của <see cref="PocNavItem.ParseList"/>.
    /// </summary>
    public static List<string> ParseList(JsonElement element)
    {
        var result = new List<string>();

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var raw = element.GetString() ?? string.Empty;
                if (raw.TrimStart().StartsWith('['))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        return ParseList(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                        // không phải JSON thật — coi như danh sách phân cách bằng dấu phẩy
                    }
                }
                AddRange(result, SplitCsv(raw));
                break;

            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                        AddRange(result, SplitCsv(entry.GetString() ?? string.Empty));
                    else if (entry.ValueKind == JsonValueKind.Object)
                        AddRange(result, SplitCsv(LabelOf(entry) ?? string.Empty));
                }
                break;
        }

        return result;
    }

    /// <summary>Tách "Manager, HR / HoD" thành từng nhãn vai — dùng cho cả 'roles' lẫn thuộc tính data-roles.</summary>
    public static List<string> SplitCsv(string? raw) =>
        (raw ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Clean)
            .Where(x => x.Length > 0)
            .ToList();

    /// <summary>So khớp nhãn vai như shell làm (không phân biệt hoa thường / khoảng trắng thừa).</summary>
    public static string Key(string? label) => (label ?? string.Empty).Trim().ToLowerInvariant();

    private static void AddRange(List<string> sink, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (sink.Count >= MaxRoles)
                return;
            if (value.Length > 0 && !sink.Any(x => Key(x) == Key(value)))
                sink.Add(value);
        }
    }

    private static string Clean(string raw)
    {
        var text = raw.Trim().Trim('"', '\'').Trim();
        return text.Length <= MaxLabelLength ? text : text[..MaxLabelLength].Trim();
    }

    private static string? LabelOf(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;
            if (string.Equals(prop.Name, "label", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "role", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, "title", StringComparison.OrdinalIgnoreCase))
                return prop.Value.GetString();
        }
        return null;
    }
}
