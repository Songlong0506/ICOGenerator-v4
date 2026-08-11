using System.Globalization;
using System.Text;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tên file cho các bản xuất người dùng tải về. Chuỗi này đi qua header <c>Content-Disposition</c> và trở
/// thành tên file trên đĩa của mọi hệ điều hành, nên chỉ giữ chữ/số ASCII (bỏ dấu tiếng Việt) — thoát bằng
/// dấu hỏi thì mọi bản tải về của các dự án tiếng Việt trông giống hệt nhau.
/// </summary>
public static class ExportFileName
{
    private const int MaxSlugLength = 60;

    /// <summary>Rút tên dự án thành slug ASCII; rỗng/không còn ký tự nào dùng được ⇒ <c>du-an</c>.</summary>
    public static string Slug(string? projectName)
    {
        var slug = new StringBuilder();
        foreach (var ch in (projectName ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            // đ/Đ là chữ cái riêng, không phải "d + dấu" — FormD không tách nó ra, nên không xử tay thì
            // "đào tạo" thành "ao-tao".
            if (ch is 'đ' or 'Đ')
                slug.Append('d');
            else if (char.IsAsciiLetterOrDigit(ch))
                slug.Append(char.ToLowerInvariant(ch));
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        var name = slug.ToString().Trim('-');
        if (name.Length > MaxSlugLength)
            name = name[..MaxSlugLength].TrimEnd('-');

        return name.Length == 0 ? "du-an" : name;
    }

    /// <summary>Ghép <c>{prefix}_{slug}_{yyyyMMdd-HHmm}{extension}</c> theo giờ địa phương của máy chủ.</summary>
    public static string Build(string prefix, string? projectName, DateTime exportedAtUtc, string extension) =>
        $"{prefix}_{Slug(projectName)}_{DateTime.SpecifyKind(exportedAtUtc, DateTimeKind.Utc).ToLocalTime():yyyyMMdd-HHmm}{extension}";
}
