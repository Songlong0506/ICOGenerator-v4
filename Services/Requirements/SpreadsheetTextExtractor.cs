using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bóc một file bảng tính (Excel .xlsx / .csv) thành TEXT CÓ CẤU TRÚC cho BA đọc: tên sheet, tiêu đề cột
/// và vài dòng mẫu — thứ mà ảnh chụp Excel làm mất (cột/kiểu/giá trị mẫu). Người dùng nghiệp vụ sống trong
/// Excel nên đây thường là nguồn fidelity cao nhất; chính prompt chat cũng mời họ "đính kèm ảnh chụp Excel".
/// Đọc file .xlsx (chuẩn OpenXML — thư viện DocumentFormat.OpenXml đã có sẵn) hoặc .csv; giới hạn số sheet/
/// dòng/cột để một file khổng lồ không thổi phồng ngữ cảnh. Best-effort: file hỏng/không đọc được ⇒ trả null,
/// caller giữ nguyên file gốc và bỏ qua phần text (như PDF không bóc được).
/// </summary>
public static class SpreadsheetTextExtractor
{
    private const int MaxSheets = 8;
    private const int MaxRowsPerSheet = 30;   // tiêu đề + tối đa 29 dòng mẫu — đủ để BA nắm cấu trúc, không đốt token.
    private const int MaxCols = 40;
    private const int MaxCellChars = 200;

    public static readonly string[] Extensions = { ".xlsx", ".xlsm", ".csv" };

    public static bool IsSpreadsheet(string? contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (Extensions.Contains(ext))
            return true;
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct.Contains("spreadsheetml")            // .xlsx
            || ct == "application/vnd.ms-excel"        // .xls/.csv đôi khi gắn nhãn này
            || ct == "text/csv";
    }

    /// <summary>Bóc text từ bytes. Trả null nếu không đọc được gì (file hỏng / rỗng).</summary>
    public static string? Extract(byte[] bytes, string fileName)
    {
        try
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var text = ext == ".csv" ? ExtractCsv(bytes) : ExtractXlsx(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null; // best-effort: hỏng thì bỏ qua phần text, giữ file gốc.
        }
    }

    private static string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets == null)
            return string.Empty;

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        var sb = new StringBuilder();
        var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().Take(MaxSheets).ToList();
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { } relId || workbookPart.GetPartById(relId) is not WorksheetPart wsPart)
                continue;

            var rows = wsPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().Take(MaxRowsPerSheet).ToList()
                       ?? new List<Row>();
            if (rows.Count == 0)
                continue;

            sb.AppendLine($"### Sheet: {sheet.Name?.Value ?? "(không tên)"}");
            foreach (var row in rows)
            {
                var cells = row.Elements<Cell>().Take(MaxCols)
                    .Select(c => CellText(c, sharedStrings));
                sb.AppendLine(string.Join(" | ", cells));
            }
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    private static string CellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(value, out var idx)
            && sharedStrings != null
            && idx >= 0 && idx < sharedStrings.ChildElements.Count)
        {
            value = sharedStrings.ChildElements[idx].InnerText;
        }
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > MaxCellChars ? value[..MaxCellChars] + "…" : value;
    }

    private static string ExtractCsv(byte[] bytes)
    {
        var content = DecodeText(bytes);
        var lines = content.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Take(MaxRowsPerSheet)
            .ToList();
        if (lines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var cells = SplitCsvLine(line).Take(MaxCols)
                .Select(c => c.Length > MaxCellChars ? c[..MaxCellChars] + "…" : c);
            sb.AppendLine(string.Join(" | ", cells));
        }
        return sb.ToString().Trim();
    }

    // Tách một dòng CSV có tôn trọng dấu nháy kép (RFC 4180 tối giản: "" là một dấu nháy escape).
    private static IEnumerable<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }

    // BOM UTF-8 hay gặp với CSV xuất từ Excel; bỏ nó để không lẫn ký tự lạ vào ô đầu.
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }
}
