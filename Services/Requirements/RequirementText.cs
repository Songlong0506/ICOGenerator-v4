namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Ba phép biến đổi chuỗi mà luồng yêu cầu dùng đi dùng lại: chuẩn hóa để SO KHỚP, cắt độ dài, và ép về
/// một dòng. Trước đây mỗi bảng chốt (<c>*MapBuilder</c>, <c>PermissionMatrixBuilder</c>) và mỗi bộ dựng
/// bản xuất (<c>ChatExportBuilder</c>, <c>ReviewPackageBuilder</c>) tự giữ một bản sao <c>private static</c>
/// giống hệt nhau — và chúng đã bắt đầu trôi lệch: ba bản có chốt null, ba bản không, nên cùng một dữ liệu
/// vào hai bảng khác nhau lại cho kết quả khớp/không khớp khác nhau. Gộp về một chỗ, lấy bản an toàn null.
///
/// Dùng qua <c>using static ICOGenerator.Services.Requirements.RequirementText;</c> để chỗ gọi giữ nguyên
/// tên ngắn <c>Normalize(...)</c> / <c>Clip(...)</c>.
/// </summary>
public static class RequirementText
{
    /// <summary>
    /// Dạng chuẩn để SO KHỚP hai chuỗi do người/LLM nhập: thường hóa, gộp mọi khoảng trắng (kể cả tab,
    /// xuống dòng) về một dấu cách, và bỏ dấu câu ở hai đầu. Không dùng để hiển thị.
    /// </summary>
    public static string Normalize(string? value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    /// <summary>Cắt cứng về tối đa <paramref name="max"/> ký tự (không thêm dấu ba chấm).</summary>
    public static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;

    /// <summary>
    /// Gộp về MỘT dòng. Các chuỗi này được nhúng vào bullet/blockquote Markdown: một ký tự xuống dòng là
    /// đủ để phần sau rơi ra ngoài khối và đọc như một đoạn văn riêng.
    /// </summary>
    public static string OneLine(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return string.Join(" ", value.Replace("\r", " ").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())).Trim();
    }
}
