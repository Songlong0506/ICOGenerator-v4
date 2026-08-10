using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đọc "dữ liệu mẫu THẬT" của một project: text đã bóc từ các tài liệu NGUỒN có cấu trúc bảng
/// (Excel/CSV, và Word — biểu mẫu Word render thành dòng "ô | ô"). Ảnh và PDF không vào đây: text bóc
/// từ PDF là văn xuôi, đưa vào chỗ "dữ liệu mẫu" chỉ làm nhiễu chứ không thành bản ghi seed được.
/// <para>
/// Dùng ở HAI đầu của cùng một sợi dây, và phải là CÙNG một hàm để hai đầu không lệch nhau:
/// <see cref="RequirementDocsService"/> nạp nó vào prompt sinh AI Design Spec (để POC demo bằng đúng
/// danh mục của đơn vị yêu cầu), còn <c>WorkspaceTools.AuditPocContent</c> nạp nó làm CHUẨN ĐỐI CHIẾU
/// (<see cref="Services.Artifacts.PocSampleDataCheck"/>) để biết POC có thật sự dùng dữ liệu đó không.
/// </para>
/// </summary>
public static class RealSampleDataReader
{
    private const int MaxFiles = 5;
    private const int MaxCharsPerFile = 3000;

    /// <summary>Trả về khối text mẫu (mỗi file một khối "[Trích từ …]"), hoặc <c>null</c> khi project chưa có tài liệu bảng nào.</summary>
    public static async Task<string?> ReadAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken = default)
    {
        var sources = await db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId
                        && (s.Kind == SourceFileKind.Spreadsheet || s.Kind == SourceFileKind.Document)
                        && s.ExtractedText != null)
            .OrderBy(s => s.CreatedAt)
            .Take(MaxFiles)
            .Select(s => new { s.FileName, s.ExtractedText, s.ColumnMap })
            .ToListAsync(cancellationToken);

        if (sources.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            var text = KeepConfirmedColumns(DataRowsOnly(s.ExtractedText!), s.ColumnMap).Trim();
            if (text.Length > MaxCharsPerFile)
                text = text[..MaxCharsPerFile] + "\n…(đã cắt bớt)";
            sb.AppendLine($"[Trích từ {s.FileName}]");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Với text bóc từ bảng tính, chỉ lấy khối <see cref="SpreadsheetTextExtractor.DataRowsHeading"/> trở đi.
    /// <para>
    /// Hai consumer của cùng một chuỗi text muốn hai nửa NGƯỢC NHAU, và đó là lý do cần cắt ở đây:
    /// <see cref="SourceContextBuilder"/> gửi text cho BA đọc nên cần khối <c>Thống kê cột</c> đứng trước để
    /// nó sống sót qua trần 20.000 ký tự; còn chỗ này cần **bản ghi thật** — tên danh mục, mã, giá trị mà POC
    /// sẽ seed lên màn hình, và mà <c>PocSampleDataCheck</c> lấy token ra để kiểm POC có dùng dữ liệu thật
    /// không. Trần ở đây chỉ 3.000 ký tự, nên nếu để nguyên thì với một bảng nhiều cột phần thống kê ăn gần
    /// hết ngân sách và cái đi tới POC là mấy dòng "có giá trị 262/262 · ĐỦ 5 giá trị" — token đặc trưng rút
    /// ra được sẽ là từ vựng thống kê chứ không phải danh mục của đơn vị yêu cầu, tức là POC seed sai mà cổng
    /// kiểm cũng mù theo.
    /// </para>
    /// Không tìm thấy mốc (text từ Word, hoặc bảng tính bóc bởi phiên bản cũ) ⇒ giữ nguyên toàn bộ text.
    /// </summary>
    /// <summary>
    /// Giữ lại ĐÚNG các cột người dùng đã chốt là dùng trong ứng dụng mới (bảng cột — xem
    /// <see cref="SourceColumnMapBuilder"/>). Chưa chốt bảng nào ⇒ giữ nguyên toàn bộ, đúng hành vi cũ.
    ///
    /// <para>
    /// Đây là chỗ bảng cột trả tiền cho chính nó. Chuỗi này là "dữ liệu mẫu thật" nạp vào prompt AI Design
    /// Spec, và POC seed màn hình bằng đúng những cột có mặt ở đây: không lọc thì người dùng đã bỏ tích
    /// <c>Revision Number</c> ở bảng cột vẫn mở demo ra và thấy nó nằm như một trường của app mới — cùng
    /// mất niềm tin ấy, chỉ đi bằng cửa sau. <c>PocSampleDataCheck</c> đọc cùng chuỗi này làm chuẩn đối
    /// chiếu nên cổng kiểm cũng siết theo đúng tập cột đã chốt.
    /// </para>
    ///
    /// <para>
    /// Chỉ đụng tới các dòng có ĐÚNG số ô như hàng tiêu đề. Bảng nhiều sheet để lại phần text của các sheet
    /// sau (kể cả khối thống kê của chúng) trong cùng chuỗi này, và những dòng đó không cùng số cột —
    /// cắt chúng theo chỉ số cột của sheet đầu là băm nát dữ liệu của sheet khác.
    /// </para>
    /// </summary>
    private static string KeepConfirmedColumns(string dataRows, string? columnMapJson)
    {
        var used = SourceColumnMapBuilder.UsedColumns(columnMapJson);
        if (used.Count == 0)
            return dataRows;

        var lines = dataRows.Replace("\r\n", "\n").Split('\n');
        var header = Array.FindIndex(lines, l => l.Contains(CellSeparator, StringComparison.Ordinal));
        if (header < 0)
            return dataRows;

        var headerCells = SplitCells(lines[header]);
        var keep = Enumerable.Range(0, headerCells.Length)
            .Where(i => used.Any(u => string.Equals(u, headerCells[i], StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Không cột nào của bảng cột khớp hàng tiêu đề (file đã bị thay bằng bản khác, hoặc bảng cột thuộc
        // sheet khác) ⇒ giữ nguyên. Cắt sạch dữ liệu mẫu tệ hơn nhiều so với để lọt vài cột thừa.
        if (keep.Count == 0)
            return dataRows;

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var cells = SplitCells(lines[i]);
            sb.AppendLine(i >= header && cells.Length == headerCells.Length
                ? string.Join(CellSeparator, keep.Select(k => cells[k]))
                : lines[i]);
        }
        return sb.ToString();
    }

    private const string CellSeparator = " | ";

    private static string[] SplitCells(string line)
        => line.Split(CellSeparator).Select(c => c.Trim()).ToArray();

    private static string DataRowsOnly(string extractedText)
    {
        var at = extractedText.IndexOf(SpreadsheetTextExtractor.DataRowsHeading, StringComparison.Ordinal);
        if (at < 0)
            return extractedText;

        // Bỏ chính dòng tiêu đề khối: nó là chỉ dẫn cho BA, không phải dữ liệu.
        var lineEnd = extractedText.IndexOf('\n', at);
        return lineEnd < 0 ? extractedText : extractedText[(lineEnd + 1)..];
    }
}
