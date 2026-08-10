using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng cột" của một tài liệu nguồn dạng bảng tính — bảng mà người dùng tích chọn cột
/// nào ứng dụng mới dùng và sửa lại cách hiểu từng cột (xem <see cref="SourceColumnNote"/>).
///
/// <para>
/// Lớp này là CHỐT CHẶN TẤT ĐỊNH của cả tính năng, không phải một bộ tiện ích: mọi tên cột đi vào bảng
/// đều phải khớp một tên cột THẬT đọc từ khối <see cref="SpreadsheetTextExtractor.ColumnStatsHeading"/>
/// của chính file đó. Không có nó, hai đường hỏng khác nhau cùng dẫn tới một chỗ:
/// </para>
/// <list type="bullet">
///   <item>model bịa ra một cột không có trong file (hoặc viết sai chính tả tên cột) ⇒ người dùng tích một
///   dòng vô nghĩa, rồi <see cref="RealSampleDataReader"/> đi lọc theo cái tên không khớp cột nào và cắt
///   sạch dữ liệu mẫu đi tới POC;</item>
///   <item>model chỉ nêu vài cột nó thấy đáng nói ⇒ các cột còn lại biến mất khỏi bảng và bị coi là "của hệ
///   cũ" mà người dùng chưa từng nhìn thấy để phản đối — đúng khiếm khuyết của hàng chip mà bảng sinh ra
///   để chữa. Vì vậy <see cref="Build"/> luôn BỔ SUNG các cột còn thiếu vào cuối bảng.</item>
/// </list>
///
/// <para>
/// Bảng chỉ được dựng khi model thật sự có đề xuất (ít nhất một dòng hợp lệ). Một bảng 18 dòng toàn ô ý
/// nghĩa trống là lượt hỏng, không phải lượt "để người dùng tự điền" — xem ghi chú ở <see cref="SourceColumnNote"/>.
/// </para>
/// </summary>
public static class SourceColumnMapBuilder
{
    /// <summary>Trần số dòng của bảng — bằng trần số cột của <see cref="SpreadsheetTextExtractor"/>.</summary>
    public const int MaxColumns = 40;

    private const int MaxMeaningChars = 200;
    private const int MaxColumnNameChars = 200;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Tên các cột THẬT của một file, đọc từ khối "Thống kê cột" của text đã bóc (mỗi dòng là
    /// <c>- {tên cột}: {mô tả}</c>). Bảng nhiều sheet có nhiều khối ⇒ gộp lại, giữ thứ tự xuất hiện và bỏ
    /// trùng. Text không có khối nào (Word, PDF, hoặc bảng tính bóc bởi phiên bản cũ) ⇒ danh sách rỗng, và
    /// khi đó KHÔNG có bảng cột nào được dựng.
    /// </summary>
    public static List<string> ReadColumnNames(string? extractedText)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(extractedText))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inBlock = false;
        foreach (var raw in extractedText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(SpreadsheetTextExtractor.ColumnStatsHeading, StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }
            if (!inBlock)
                continue;
            // Khối kết thúc ở dòng trống hoặc tiêu đề kế tiếp; sheet sau sẽ mở lại khối của nó.
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                inBlock = false;
                continue;
            }
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            // "- {tên}: {mô tả}" — cắt ở dấu ": " ĐẦU TIÊN. Tên cột có chứa ": " sẽ bị cắt sai, khi đó dòng
            // model đề xuất cho cột đó không khớp và bị bỏ; cột vẫn có mặt trong bảng (dưới cái tên đã cắt)
            // để người dùng tự đánh dấu, tức mất một đề xuất chứ không mất cả cột.
            var body = line[2..];
            var at = body.IndexOf(": ", StringComparison.Ordinal);
            var name = (at < 0 ? body : body[..at]).Trim();
            if (name.Length == 0 || name.Length > MaxColumnNameChars || !seen.Add(name))
                continue;

            result.Add(name);
            if (result.Count >= MaxColumns)
                break;
        }

        return result;
    }

    /// <summary>
    /// Bảng cột cuối cùng cho MỘT file: giữ các đề xuất khớp cột thật (lấy lại đúng chính tả trong file),
    /// bỏ dòng bịa/trùng, rồi bổ sung mọi cột chưa được nhắc tới với <see cref="SourceColumnNote.Used"/> =
    /// false. Thứ tự luôn theo thứ tự cột TRONG FILE — người dùng đang đối chiếu với chính bảng tính của họ.
    /// Trả về rỗng khi file không có cột thật nào, hoặc khi không đề xuất nào khớp (xem ghi chú class).
    /// </summary>
    public static List<SourceColumnNote> Build(string fileName, IEnumerable<SourceColumnNote>? proposed, IReadOnlyList<string> realColumns)
    {
        var result = new List<SourceColumnNote>();
        if (realColumns.Count == 0)
            return result;

        var byColumn = new Dictionary<string, SourceColumnNote>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in proposed ?? Enumerable.Empty<SourceColumnNote>())
        {
            var column = (note?.Column ?? string.Empty).Trim();
            if (column.Length == 0 || byColumn.ContainsKey(column))
                continue;
            // Đề xuất cho một file KHÁC trong cùng lượt upload: bỏ qua ở đây, nó thuộc bảng của file kia.
            var file = (note!.FileName ?? string.Empty).Trim();
            if (file.Length > 0 && !string.Equals(file, fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!realColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
                continue;

            byColumn[column] = note;
        }

        if (byColumn.Count == 0)
            return result;

        foreach (var column in realColumns.Take(MaxColumns))
        {
            byColumn.TryGetValue(column, out var note);
            result.Add(new SourceColumnNote
            {
                FileName = fileName,
                Column = column, // chính tả của FILE, không phải của model
                Meaning = Clip((note?.Meaning ?? string.Empty).Trim()),
                Used = note?.Used ?? false
            });
        }

        return result;
    }

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT (người dùng vừa tích/sửa rồi gửi lên). Cùng luật với
    /// <see cref="Build"/> — server không tin danh sách cột do client gửi, kể cả khi nó vừa được server
    /// render ra vài giây trước.
    /// </summary>
    public static List<SourceColumnNote> Sanitize(string fileName, IEnumerable<SourceColumnNote>? submitted, IReadOnlyList<string> realColumns)
        => Build(fileName, submitted, realColumns);

    /// <summary>Đọc JSON bảng cột đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<SourceColumnNote> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<SourceColumnNote>();

        try
        {
            var notes = JsonSerializer.Deserialize<List<SourceColumnNote>>(json, ReadOptions) ?? new List<SourceColumnNote>();
            return notes.Where(n => n != null && !string.IsNullOrWhiteSpace(n.Column)).ToList();
        }
        catch
        {
            return new List<SourceColumnNote>();
        }
    }

    /// <summary>Tên các cột người dùng đã chốt là DÙNG trong ứng dụng mới. Rỗng khi chưa chốt bảng nào.</summary>
    public static List<string> UsedColumns(string? columnMapJson)
        => Parse(columnMapJson).Where(n => n.Used).Select(n => n.Column.Trim()).ToList();

    /// <summary>
    /// Khối ngữ cảnh gửi kèm nguồn ở MỌI lượt chat sau: cột nào đã chốt là dùng (kèm nghĩa người dùng đã
    /// duyệt), cột nào là của hệ cũ. Không có khối này thì bảng chỉ là một màn bấm đẹp — BA vẫn hỏi lại
    /// đúng các cột người dùng vừa giải nghĩa, và vẫn dựng yêu cầu trên các cột đã bị loại.
    /// Trả null khi chưa chốt.
    /// </summary>
    public static string? RenderConfirmedBlock(string fileName, string? columnMapJson)
    {
        var notes = Parse(columnMapJson);
        if (notes.Count == 0)
            return null;

        var used = notes.Where(n => n.Used).ToList();
        var dropped = notes.Where(n => !n.Used).Select(n => n.Column.Trim()).Where(c => c.Length > 0).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"\n--- Bảng cột của \"{fileName}\" đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại nghĩa các cột này) ---");
        if (used.Count > 0)
        {
            sb.AppendLine("Cột DÙNG trong ứng dụng mới:");
            foreach (var note in used)
            {
                var meaning = note.Meaning.Trim();
                sb.AppendLine(meaning.Length > 0 ? $"- {note.Column}: {meaning}" : $"- {note.Column}");
            }
        }
        else
        {
            sb.AppendLine("Người dùng KHÔNG chọn cột nào của file này là cột nghiệp vụ của ứng dụng mới.");
        }

        if (dropped.Count > 0)
        {
            sb.AppendLine("Cột KHÔNG dùng — dữ liệu của hệ thống cũ. Đừng đưa vào yêu cầu, màn hình hay dữ liệu mẫu, "
                + "và đừng hỏi thêm về chúng: " + string.Join(", ", dropped));
        }

        return sb.ToString().TrimEnd();
    }

    private static string Clip(string value)
        => value.Length > MaxMeaningChars ? value[..MaxMeaningChars] : value;
}
