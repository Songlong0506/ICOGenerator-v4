namespace ICOGenerator.Localization;

/// <summary>
/// Một resource chở MỘT DANH SÁCH cụm từ (các cách model hay viết thay cho một mã — "toàn bộ", "mọi") được
/// ghi thành một chuỗi, các cụm ngăn bằng <c>|</c>. Danh sách kiểu này là lưới đỡ theo từng ngôn ngữ, nên
/// nó thuộc resource chứ không thuộc code.
/// </summary>
public static class LocList
{
    /// <summary>Tách chuỗi resource thành các cụm, đã bỏ khoảng trắng thừa và đưa về chữ thường.</summary>
    public static IEnumerable<string> Split(string? resource)
        => (resource ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant());

    /// <summary>Các cụm của <paramref name="key"/> ở MỌI ngôn ngữ được hỗ trợ, gộp lại và bỏ trùng.</summary>
    public static IReadOnlyList<string> All(string key)
        => Loc.InAllLanguages(key).SelectMany(Split).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Các cụm của <paramref name="key"/> ở mọi ngôn ngữ, ghép thành một nhánh <c>a|b|c</c> đã escape để
    /// nhét vào một biểu thức chính quy cạnh các từ tiếng Anh cố định của nó.
    /// </summary>
    public static string Alternation(string key)
        => string.Join("|", All(key).Select(System.Text.RegularExpressions.Regex.Escape));
}
