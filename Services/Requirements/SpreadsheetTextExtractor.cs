using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bóc một file bảng tính (Excel .xlsx / .csv) thành TEXT CÓ CẤU TRÚC cho BA đọc: tên sheet, tiêu đề cột,
/// vài chục dòng mẫu, và **thống kê từng cột tính trên TOÀN BỘ bảng** — thứ mà ảnh chụp Excel làm mất
/// (cột/kiểu/giá trị mẫu). Người dùng nghiệp vụ sống trong Excel nên đây thường là nguồn fidelity cao nhất;
/// chính prompt chat cũng mời họ "đính kèm ảnh chụp Excel".
/// Đọc file .xlsx (chuẩn OpenXML — thư viện DocumentFormat.OpenXml đã có sẵn) hoặc .csv; giới hạn số sheet/
/// dòng/cột để một file khổng lồ không thổi phồng ngữ cảnh. Best-effort: file hỏng/không đọc được ⇒ trả null,
/// caller giữ nguyên file gốc và bỏ qua phần text (như PDF không bóc được).
///
/// Vì sao có mục "Thống kê cột" chứ không chỉ dòng mẫu: BA đọc bảng để rút ra DANH MỤC của từng cột (chúng
/// thành enum/danh mục trong mô hình dữ liệu ở các bước sau), mà vài chục dòng đầu của một bản xuất thường
/// được sắp theo người/đơn vị nên KHÔNG đại diện cho cả bảng. Ca thật: file 262 dòng, 29 dòng đầu chỉ chứa
/// Assignment Type "REQ"/"MAN" nên bản đọc lại của BA bỏ sót "OPT" — đúng giá trị mã hóa "khóa học tự chọn"
/// mà người dùng đã nói ngay câu đầu tiên; cột Required Date trống sạch trong 29 dòng đầu nhưng có 12 dòng
/// mang hạn hoàn thành ở phía dưới. Thống kê quét cả bảng tốn vài trăm token và chặn đúng loại lỗi đó.
/// </summary>
public static class SpreadsheetTextExtractor
{
    private const int MaxSheets = 8;
    private const int MaxSampleRows = 30;      // tiêu đề + tối đa 29 dòng mẫu — đủ để BA nắm hình dạng bảng.
    private const int MaxProfiledRows = 50_000; // trần quét thống kê: file khổng lồ vẫn kết thúc trong chớp mắt.
    private const int MaxCols = 40;
    private const int MaxCellChars = 200;

    // Dò hàng tiêu đề (xem ChooseHeaderRow): đệm HeaderScanRows dòng đầu sheet, chỉ xét HeaderCandidateRows
    // dòng đầu tiên trong đệm làm ứng viên — phần còn lại của đệm là "phía dưới" để chấm điểm.
    private const int HeaderScanRows = 25;
    private const int HeaderCandidateRows = 12;
    private const int MaxHeaderNameChars = 60;  // dài hơn thế là một câu mô tả, không phải tên cột.
    private const int HeaderScoreMargin = 2;    // biên để BỎ dòng trên mà xuống dòng dưới — xem ChooseHeaderRow.
    private const int MaxPreambleLines = 5;     // các dòng trước hàng tiêu đề được kể lại, không bị vứt.
    private const int MaxPreambleLineChars = 160;

    // Cột ít giá trị ⇒ liệt kê HẾT (đó là một danh mục, thiếu một giá trị là thiếu một ca nghiệp vụ).
    // Cột nhiều giá trị ⇒ chỉ vài giá trị hay gặp nhất, kèm tổng số giá trị phân biệt.
    private const int ListAllValuesUpTo = 12;
    private const int TopValuesWhenMany = 5;
    private const int MaxTrackedDistinct = 2_000; // chặn cột dạng khóa (mọi dòng một giá trị) ăn hết bộ nhớ.
    private const int MaxValueChars = 60;

    public static readonly string[] Extensions = { ".xlsx", ".xlsm", ".csv" };

    /// <summary>Tiêu đề khối thống kê cột — tính trên toàn bộ bảng. Đứng TRƯỚC khối dòng dữ liệu.</summary>
    public const string ColumnStatsHeading = "#### Thống kê cột";

    /// <summary>
    /// Nhãn của các dòng nằm TRƯỚC hàng tiêu đề (banner, người tạo, ngày cập nhật). Có nhãn riêng để bản
    /// đọc lại không tưởng nhầm chúng là tên cột — đúng cái bẫy khiến chúng từng bị chốt thành hàng tiêu đề.
    /// </summary>
    public const string PreambleHeading = "Dòng đầu sheet (trước hàng tiêu đề):";

    /// <summary>
    /// Tiêu đề khối dòng dữ liệu (hàng tiêu đề + các dòng mẫu). Là hằng số vì có consumer đi TÌM đúng khối
    /// này: <see cref="RealSampleDataReader"/> cần BẢN GHI THẬT chứ không cần thống kê — xem chú thích ở đó.
    /// </summary>
    public const string DataRowsHeading = "#### Dòng dữ liệu";

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

            var rows = wsPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? Enumerable.Empty<Row>();
            var table = Read(rows.Select(r => RowCells(r, sharedStrings)));
            if (table.IsEmpty)
                continue;

            sb.AppendLine($"### Sheet: {sheet.Name?.Value ?? "(không tên)"}");
            Render(sb, table);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Trải một dòng OpenXML thành mảng ô ĐÚNG VỊ TRÍ CỘT. Bắt buộc phải đi qua <c>CellReference</c>: OpenXML
    /// bỏ hẳn phần tử của ô rỗng, nên đọc tuần tự <c>row.Elements&lt;Cell&gt;()</c> thì mọi ô sau một chỗ
    /// trống bị TRƯỢT sang trái. Ca thật: dòng thiếu ô "Active User" làm tên người rơi vào cột đó, và bản
    /// đọc lại của BA báo với người dùng rằng "dữ liệu có vẻ bị lệch giữa hàng tiêu đề và các giá trị" —
    /// bảng gốc không lệch chút nào, chỉ phần text ta dựng ra mới lệch.
    /// Ô không có <c>CellReference</c> (một số bộ sinh file, và các .xlsx dựng trong test) ⇒ rơi về vị trí
    /// tuần tự ngay sau ô trước đó.
    /// </summary>
    private static string[] RowCells(Row row, SharedStringTable? sharedStrings)
    {
        var cells = new string[MaxCols];
        var next = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var col = ColumnIndex(cell.CellReference?.Value) ?? next;
            if (col >= MaxCols)
                continue;
            cells[col] = CellText(cell, sharedStrings);
            next = col + 1;
        }
        return cells;
    }

    /// <summary>"AB12" ⇒ 27 (0-based). Trả null khi không có/không đọc được phần chữ cái.</summary>
    private static int? ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
            return null;

        var index = 0;
        var letters = 0;
        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') index = index * 26 + (ch - 'a' + 1);
            else break;
            letters++;
        }
        return letters == 0 ? null : index - 1;
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
        var lines = content.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0);

        var table = Read(lines.Select(l =>
        {
            var cells = new string[MaxCols];
            var i = 0;
            foreach (var field in SplitCsvLine(l))
            {
                if (i >= MaxCols) break;
                cells[i++] = field.Length > MaxCellChars ? field[..MaxCellChars] + "…" : field;
            }
            return cells;
        }));

        if (table.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        Render(sb, table);
        return sb.ToString().Trim();
    }

    // ---- Đọc một lượt: giữ dòng mẫu, đồng thời dồn thống kê cho TOÀN BỘ bảng (không giữ lại dòng nào khác) --

    private sealed class Table
    {
        public string[] Header = Array.Empty<string>();
        public List<string> Preamble = new();
        public List<string[]> Sample = new();
        public List<ColumnProfile> Columns = new();
        public int DataRows;
        public bool Truncated;
        public bool IsEmpty => Header.Length == 0;
    }

    private sealed class ColumnProfile
    {
        public string Name = string.Empty;
        public int Filled;
        public readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
        public bool CountsOverflowed;
    }

    private static Table Read(IEnumerable<string[]> rows)
    {
        var table = new Table();

        using var rest = rows.GetEnumerator();
        var window = new List<string[]>(HeaderScanRows);
        while (window.Count < HeaderScanRows && rest.MoveNext())
            window.Add(rest.Current);

        var headerIndex = ChooseHeaderRow(window);
        if (headerIndex < 0)
            return table; // sheet rỗng.

        // Các dòng trước hàng tiêu đề (banner, "Created by", ngày cập nhật) không phải dữ liệu, nhưng cũng
        // không phải rác: chúng nói file này là bản gì, của ai, kỳ nào. Kể lại nguyên văn, gắn nhãn đúng
        // thân phận, thay vì để chúng đội lốt tên cột.
        table.Preamble = window
            .Take(headerIndex)
            .Select(r => string.Join(" | ", r.Take(Math.Max(LastNonEmpty(r) + 1, 0)).Select(c => c ?? string.Empty)))
            .Where(l => l.Any(ch => ch != ' ' && ch != '|'))
            .Select(l => l.Length > MaxPreambleLineChars ? l[..MaxPreambleLineChars] + "…" : l)
            .Take(MaxPreambleLines)
            .ToList();

        // Bề rộng lấy theo dòng RỘNG NHẤT trong đệm kể từ hàng tiêu đề, không lấy theo riêng hàng tiêu đề:
        // biểu mẫu thật hay có cột chỉ có dữ liệu mà không có tiêu đề (hoặc tiêu đề nằm ở dòng gợi ý ngay
        // dưới). Cắt theo hàng tiêu đề thì các cột đó biến mất hẳn khỏi bản đọc; để lại dưới tên "(cột n)"
        // thì người dùng còn nhìn thấy giá trị mà gọi tên chúng ở bảng cột.
        var width = 0;
        for (var i = headerIndex; i < window.Count; i++)
            width = Math.Max(width, LastNonEmpty(window[i]) + 1);
        if (width <= 0)
            return table;

        table.Header = window[headerIndex].Take(width).Select(c => c ?? string.Empty).ToArray();
        for (var i = 0; i < width; i++)
            table.Columns.Add(new ColumnProfile { Name = ColumnName(table.Header[i], i) });

        foreach (var cells in Body(window, headerIndex, rest))
        {
            if (LastNonEmpty(cells) < 0)
                continue; // dòng trống xen giữa bảng: không phải bản ghi, đừng thổi số dòng lẫn dòng mẫu.

            if (table.DataRows >= MaxProfiledRows)
            {
                table.Truncated = true;
                break;
            }

            table.DataRows++;
            if (table.Sample.Count < MaxSampleRows - 1)
                table.Sample.Add(cells.Take(width).Select(c => c ?? string.Empty).ToArray());

            for (var i = 0; i < width; i++)
            {
                var value = (cells[i] ?? string.Empty).Trim();
                if (value.Length == 0)
                    continue;

                var col = table.Columns[i];
                col.Filled++;
                if (col.Counts.TryGetValue(value, out var n))
                    col.Counts[value] = n + 1;
                else if (col.Counts.Count < MaxTrackedDistinct)
                    col.Counts[value] = 1;
                else
                    col.CountsOverflowed = true;
            }
        }

        return table;
    }

    /// <summary>Các dòng dữ liệu: phần còn lại của đệm sau hàng tiêu đề, rồi tới phần chưa đọc của sheet.</summary>
    private static IEnumerable<string[]> Body(List<string[]> window, int headerIndex, IEnumerator<string[]> rest)
    {
        for (var i = headerIndex + 1; i < window.Count; i++)
            yield return window[i];
        while (rest.MoveNext())
            yield return rest.Current;
    }

    /// <summary>
    /// Chọn hàng tiêu đề trong <paramref name="window"/> (chỉ số trong đệm), hoặc -1 khi sheet rỗng.
    ///
    /// <para>
    /// KHÔNG lấy "dòng có chữ đầu tiên". Biểu mẫu do người làm nghiệp vụ dựng gần như luôn mở đầu bằng một
    /// dòng banner một ô (<c>HcP_JD Database 2023</c>), rồi <c>Created by</c> / <c>Updated on</c>, rồi mới
    /// tới hàng tiêu đề thật ở dòng 6. Lấy dòng đầu làm tiêu đề gây ra HAI hỏng cùng lúc, và cái thứ hai
    /// im lặng: bề rộng bảng bị chốt theo dòng banner nên **mọi cột từ B trở đi bị vứt khỏi bản đọc** (12
    /// cột JD chỉ còn 1), còn cột nào sống sót thì mang tên rỗng ⇒ bảng cột bày ra cho người dùng toàn
    /// <c>(cột 1)</c>, <c>(cột 2)</c>… trong khi file có sẵn <c>Org Unit</c>, <c>Job title</c>, <c>Job Grade</c>.
    /// Người dùng nhìn thấy đúng thứ họ không thể phân xử: tên cột họ biết rõ thì không hiện, còn thứ hiện
    /// ra thì không có trong file nào cả.
    /// </para>
    ///
    /// <para>
    /// Chấm điểm mỗi ứng viên bằng số ô "trông như tên cột": có chữ, ngắn (≤ <see cref="MaxHeaderNameChars"/>),
    /// không phải số, và **không lặp lại ở phía dưới trong cùng cột** — dấu hiệu tách tiêu đề khỏi dữ liệu
    /// mạnh nhất mà không cần biết nghiệp vụ, vì giá trị của một cột danh mục thì lặp còn tên cột thì không.
    /// Điểm bằng nhau ⇒ lấy dòng TRÊN, đó là cách hàng tiêu đề thắng hàng gợi ý ("Automatically filled",
    /// "Please choose from drop down") nằm ngay dưới nó. Chỉ dòng đầu đệm mới được làm ứng viên: dòng càng
    /// gần đáy đệm càng ít dòng phía dưới để đối chiếu nên điểm bị thổi lên, và một dòng dữ liệu ở cuối đệm
    /// sẽ ăn gian thắng hàng tiêu đề thật.
    /// </para>
    ///
    /// <para>
    /// Dòng TRÊN được giữ trừ khi dòng dưới hơn hẳn <see cref="HeaderScoreMargin"/> điểm. Không có biên này
    /// thì với bảng nhỏ, dòng dữ liệu đầu tiên chỉ cần đông ô hơn hàng tiêu đề một ô (một cột không có tiêu
    /// đề là đủ) đã cướp được vai tiêu đề — mà mọi giá trị của nó chưa kịp lặp lại để bị trừ điểm.
    /// </para>
    ///
    /// Không dòng nào ăn điểm (bảng toàn số, sheet chỉ có vài ô chữ) ⇒ rơi về hành vi cũ: dòng có chữ đầu tiên.
    /// </summary>
    private static int ChooseHeaderRow(IReadOnlyList<string[]> window)
    {
        var bestIndex = -1;
        var bestScore = -1;
        var candidates = Math.Min(HeaderCandidateRows, window.Count);
        for (var i = 0; i < candidates; i++)
        {
            if (LastNonEmpty(window[i]) < 0)
                continue;

            var score = HeaderScore(window, i);
            if (bestIndex < 0 || score >= bestScore + HeaderScoreMargin)
            {
                bestIndex = i;
                bestScore = score;
            }
        }
        if (bestIndex >= 0)
            return bestIndex;

        for (var i = 0; i < window.Count; i++)
            if (LastNonEmpty(window[i]) >= 0)
                return i;
        return -1;
    }

    private static int HeaderScore(IReadOnlyList<string[]> window, int index)
    {
        var row = window[index];
        var score = 0;
        for (var c = 0; c < row.Length; c++)
        {
            var value = (row[c] ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > MaxHeaderNameChars || LooksNumeric(value))
                continue;

            var repeatedBelow = false;
            for (var j = index + 1; j < window.Count && !repeatedBelow; j++)
                repeatedBelow = string.Equals((window[j][c] ?? string.Empty).Trim(), value, StringComparison.Ordinal);

            if (!repeatedBelow)
                score++;
        }
        return score;
    }

    /// <summary>Số, ngày, phần trăm… — một ô như thế là dữ liệu, không phải tên cột.</summary>
    private static bool LooksNumeric(string value)
    {
        var hasDigit = false;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch)) hasDigit = true;
            else if (ch is not (',' or '.' or '%' or '-' or '/' or ' ')) return false;
        }
        return hasDigit;
    }

    private static int LastNonEmpty(string[] cells)
    {
        for (var i = cells.Length - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(cells[i]))
                return i;
        return -1;
    }

    private static string ColumnName(string header, int index)
        => string.IsNullOrWhiteSpace(header) ? $"(cột {index + 1})" : header.Trim();

    // ---- Render ------------------------------------------------------------------------------------------

    /// <summary>
    /// Thống kê ĐI TRƯỚC các dòng mẫu, và đó là một quyết định về ngân sách chứ không phải thẩm mỹ:
    /// <c>SourceContextBuilder.Truncate</c> cắt phần text của mỗi nguồn ở <c>Llm:SourceUpload:MaxTextCharsPerFile</c>
    /// (mặc định 20.000 ký tự) và **giữ phần ĐẦU**. Một sheet rộng (tới 40 cột, ô dài tới 200 ký tự) đủ sức
    /// ăn hết ngân sách đó chỉ bằng 29 dòng mẫu, và bảng nhiều sheet thì các sheet sau bị cắt sạch. Đặt
    /// thống kê xuống dưới là để thứ ĐẮT NHẤT của cả lượt đọc file rơi ra ngoài đầu tiên — mất danh mục cột
    /// còn tệ hơn mất vài dòng mẫu, vì dòng mẫu chỉ để thấy hình dạng còn danh mục thì không suy lại được.
    /// </summary>
    private static void Render(StringBuilder sb, Table table)
    {
        var scope = table.Truncated ? $"{table.DataRows}+ dòng đầu" : $"{table.DataRows} dòng";
        sb.AppendLine($"Tổng: {(table.Truncated ? $"trên {table.DataRows}" : table.DataRows.ToString())} dòng dữ liệu, {table.Header.Length} cột.");
        if (table.Preamble.Count > 0)
        {
            sb.AppendLine($"{PreambleHeading} {string.Join(" · ", table.Preamble)}");
        }
        sb.AppendLine();

        sb.AppendLine($"{ColumnStatsHeading} (trên {scope})");
        foreach (var col in table.Columns)
            sb.AppendLine("- " + DescribeColumn(col, table.DataRows));

        sb.AppendLine();
        sb.AppendLine(table.DataRows > table.Sample.Count
            ? $"{DataRowsHeading} ({table.Sample.Count} dòng đầu làm mẫu — chỉ để thấy hình dạng dữ liệu; ĐỪNG suy ra danh mục của cột từ đây, dùng \"Thống kê cột\" bên trên)"
            : $"{DataRowsHeading} (toàn bộ {table.DataRows} dòng)");

        sb.AppendLine(string.Join(" | ", table.Header));
        foreach (var row in table.Sample)
            sb.AppendLine(string.Join(" | ", row));
    }

    private static string DescribeColumn(ColumnProfile col, int dataRows)
    {
        if (col.Filled == 0)
            return $"{col.Name}: TRỐNG ở toàn bộ {dataRows} dòng";

        var fill = $"có giá trị {col.Filled}/{dataRows}";
        var distinct = col.Counts.Count;

        if (!col.CountsOverflowed && distinct == 1)
            return $"{col.Name}: {fill} · CHỈ MỘT giá trị duy nhất: {Clip(col.Counts.Keys.First())}";

        var top = col.Counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal);

        if (!col.CountsOverflowed && distinct <= ListAllValuesUpTo)
        {
            var all = string.Join(", ", top.Select(p => $"{Clip(p.Key)} ({p.Value})"));
            return $"{col.Name}: {fill} · ĐỦ {distinct} giá trị: {all}";
        }

        var some = string.Join(", ", top.Take(TopValuesWhenMany).Select(p => $"{Clip(p.Key)} ({p.Value})"));
        var count = col.CountsOverflowed ? $"trên {MaxTrackedDistinct}" : distinct.ToString();
        return $"{col.Name}: {fill} · {count} giá trị phân biệt · hay gặp nhất: {some}";
    }

    private static string Clip(string value)
        => value.Length > MaxValueChars ? value[..MaxValueChars] + "…" : value;

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
