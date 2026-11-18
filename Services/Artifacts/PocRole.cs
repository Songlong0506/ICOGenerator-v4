using System.Text.Json;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Một nút của khối VIEW AS: VAI (bắt buộc — thứ <c>data-roles</c> khớp theo) và, tùy chọn, NGƯỜI dùng
/// demo đóng vai đó. Hai lớp này khác nhau: quyền xem màn hình đi theo VAI của "§ 6b. Permission Matrix",
/// còn người chỉ để lọc dữ liệu "của mình" / "của đơn vị" (hai Bosch supervisor cùng vai nhưng duyệt
/// ticket khác nhau). Gắn quyền thẳng vào tên người thì gán thêm một người vào vai đó cũng không thấy gì.
/// </summary>
public sealed record PocRoleEntry(string Role, string? Persona = null)
{
    /// <summary>Nhãn hiển thị/so khớp của nút: tên người nếu có, không thì tên vai.</summary>
    public string Label => string.IsNullOrEmpty(Persona) ? Role : Persona;
}

/// <summary>
/// Danh sách VAI của bản demo — nguồn của khối VIEW AS ở cuối sidebar POC (xem
/// <see cref="PocTemplate.ReplaceRoles(string, IReadOnlyList{PocRoleEntry})"/>). Vai lấy từ "§ 6b.
/// Permission Matrix" của AI Design Spec và do agent gửi kèm <c>SetPocContent</c>; mục menu / section
/// khai báo <c>data-roles</c> theo đúng các nhãn VAI ở đây (không bao giờ theo tên người). POC không có
/// backend nên KHÔNG dựng màn đăng nhập giả: người xem demo đổi vai tại chỗ.
/// </summary>
public static class PocRole
{
    /// <summary>Trần số VAI khác nhau: một bản demo nhiều hơn thế là danh sách người dùng, không phải danh sách vai.</summary>
    public const int MaxRoles = 8;

    /// <summary>Trần số NÚT của khối VIEW AS khi mỗi vai có thể có vài người dùng demo.</summary>
    public const int MaxEntries = 12;

    /// <summary>
    /// Đọc tham số 'roles' thành các nút VIEW AS. Ngoài mọi dạng <see cref="ParseList"/> nhận (mỗi chuỗi
    /// là một VAI), một phần tử object có thể mang người dùng demo:
    /// <c>{ "role": "Bosch supervisor", "persona": "Nguyen Minh Tri" }</c> (bí danh người: "persona",
    /// "user", "person"; "name" chỉ được hiểu là người khi object đã có "role"/"label"). Trùng cặp
    /// (vai, người) bị bỏ; số vai khác nhau bị chặn ở <see cref="MaxRoles"/>, số nút ở <see cref="MaxEntries"/>.
    /// </summary>
    public static List<PocRoleEntry> ParseEntries(JsonElement element)
    {
        var result = new List<PocRoleEntry>();

        if (element.ValueKind == JsonValueKind.String
            && (element.GetString() ?? string.Empty).TrimStart().StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(element.GetString()!);
                return ParseEntries(doc.RootElement);
            }
            catch (JsonException)
            {
                // không phải JSON thật — rơi xuống nhánh chuỗi phân cách bằng dấu phẩy
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    var (role, persona) = EntryOf(entry);
                    foreach (var r in SplitCsv(role))
                        AddEntry(result, new PocRoleEntry(r, NullIfEmpty(Clean(persona ?? string.Empty))));
                }
                else if (entry.ValueKind == JsonValueKind.String)
                {
                    foreach (var r in SplitCsv(entry.GetString()))
                        AddEntry(result, new PocRoleEntry(r));
                }
            }
            return result;
        }

        foreach (var r in ParseList(element))
            AddEntry(result, new PocRoleEntry(r));
        return result;
    }

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

    private static void AddEntry(List<PocRoleEntry> sink, PocRoleEntry entry)
    {
        if (entry.Role.Length == 0 || sink.Count >= MaxEntries)
            return;
        if (sink.Any(x => Key(x.Role) == Key(entry.Role) && Key(x.Persona) == Key(entry.Persona)))
            return;
        var newRole = !sink.Any(x => Key(x.Role) == Key(entry.Role));
        if (newRole && sink.Select(x => Key(x.Role)).Distinct().Count() >= MaxRoles)
            return;
        sink.Add(entry);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    // Vai: "role" / "label" / "title" (rồi mới tới "name" khi object không có khoá nào ở trên).
    // Người: "persona" / "user" / "person", hoặc "name" khi vai đã có từ một khoá vai ở trên.
    private static (string Role, string? Persona) EntryOf(JsonElement obj)
    {
        string? role = null, persona = null, name = null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;
            var value = prop.Value.GetString();
            switch (prop.Name.ToLowerInvariant())
            {
                case "role" or "label" or "title":
                    role ??= value;
                    break;
                case "persona" or "user" or "person":
                    persona ??= value;
                    break;
                case "name":
                    name ??= value;
                    break;
            }
        }

        if (role == null)
            return (name ?? string.Empty, persona);
        return (role, persona ?? name);
    }

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
