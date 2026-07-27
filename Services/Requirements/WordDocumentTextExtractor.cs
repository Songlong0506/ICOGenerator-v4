using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bóc một file Word (.docx / .docm) thành TEXT cho BA đọc: từng đoạn văn theo thứ tự, bảng render thành
/// dòng "ô | ô | ô" (giữ được cấu trúc biểu mẫu — thứ hay chứa chính các trường dữ liệu của nghiệp vụ).
///
/// Vì sao cần: quy trình hiện tại của một phòng ban gần như luôn nằm trong file Word (quy định, biểu mẫu,
/// mô tả nghiệp vụ), nhưng trước đây upload .docx bị chặn ngay ở cổng validate — người dùng phải copy tay
/// sang chat hoặc chụp màn hình (mất cấu trúc). Cùng tinh thần best-effort như
/// <see cref="SpreadsheetTextExtractor"/>: file hỏng/không đọc được ⇒ trả null, caller giữ file gốc.
/// </summary>
public static class WordDocumentTextExtractor
{
    // Trần để một tài liệu dài không nuốt trọn cửa sổ ngữ cảnh — BA cần nắm cấu trúc và các quy tắc chính,
    // không cần nguyên văn cả cuốn quy chế.
    private const int MaxBlocks = 400;
    private const int MaxCharsPerBlock = 2000;
    private const int MaxTotalChars = 40000;

    public static readonly string[] Extensions = { ".docx", ".docm" };

    public static bool IsWordDocument(string? contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (Extensions.Contains(ext))
            return true;
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct.Contains("wordprocessingml");
    }

    /// <summary>Bóc text từ bytes. Trả null nếu không đọc được gì (file hỏng / rỗng / chỉ có ảnh).</summary>
    public static string? Extract(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return null;

            var sb = new StringBuilder();
            var blocks = 0;

            // Duyệt con TRỰC TIẾP của body theo thứ tự tài liệu để đoạn văn và bảng giữ đúng trình tự —
            // lấy riêng Paragraph rồi riêng Table sẽ dồn hết bảng xuống cuối, mất mạch "mô tả rồi tới biểu mẫu".
            foreach (var element in body.ChildElements)
            {
                if (blocks >= MaxBlocks || sb.Length >= MaxTotalChars)
                    break;

                switch (element)
                {
                    case Paragraph paragraph:
                        {
                            var text = Clean(paragraph.InnerText);
                            if (text.Length == 0)
                                continue;
                            sb.AppendLine(text);
                            blocks++;
                            break;
                        }
                    case Table table:
                        {
                            var rows = 0;
                            foreach (var row in table.Elements<TableRow>())
                            {
                                if (blocks >= MaxBlocks || sb.Length >= MaxTotalChars)
                                    break;
                                var cells = row.Elements<TableCell>().Select(c => Clean(c.InnerText));
                                var line = string.Join(" | ", cells).Trim();
                                if (line.Replace("|", string.Empty).Trim().Length == 0)
                                    continue;
                                sb.AppendLine(line);
                                blocks++;
                                rows++;
                            }
                            if (rows > 0)
                                sb.AppendLine();
                            break;
                        }
                }
            }

            var result = sb.ToString().Trim();
            if (result.Length > MaxTotalChars)
                result = result[..MaxTotalChars] + "\n…(đã cắt bớt)";
            return result.Length == 0 ? null : result;
        }
        catch
        {
            return null; // best-effort: hỏng thì bỏ qua phần text, giữ file gốc.
        }
    }

    private static string Clean(string? raw)
    {
        var text = (raw ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ");
        return text.Length > MaxCharsPerBlock ? text[..MaxCharsPerBlock] + "…" : text;
    }
}
