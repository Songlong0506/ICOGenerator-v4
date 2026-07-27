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
            .Select(s => new { s.FileName, s.ExtractedText })
            .ToListAsync(cancellationToken);

        if (sources.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            var text = s.ExtractedText!.Trim();
            if (text.Length > MaxCharsPerFile)
                text = text[..MaxCharsPerFile] + "\n…(đã cắt bớt)";
            sb.AppendLine($"[Trích từ {s.FileName}]");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
