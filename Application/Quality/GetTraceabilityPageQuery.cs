using ICOGenerator.Data;
using ICOGenerator.Services.Quality;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Quality;

public record TraceabilityProjectOption(Guid Id, string Name);

public record TraceabilityPageVm(
    IReadOnlyList<TraceabilityProjectOption> Projects,
    Guid? SelectedProjectId,
    string? SelectedProjectName,
    // ----- Ma trận đã lưu của project đang chọn (null = chưa phân tích lần nào) -----
    TraceabilityMatrix? Matrix,
    DateTime? GeneratedAt,
    string? ModelName,
    int TotalTokens,
    string? GeneratedByUsername,
    // ----- Đếm sẵn cho dải stat (tính từ Matrix, tránh view tự lặp logic) -----
    int CoveredCount,
    int PartialCount,
    int MissingCount);

/// <summary>
/// Dữ liệu trang Ma trận truy vết: danh sách project PHÂN TÍCH ĐƯỢC (đã có tài liệu yêu cầu — Product
/// Brief hoặc BRD) + ma trận đã lưu của project đang chọn. Không chọn/chọn sai thì mặc định project có
/// tài liệu mới nhất.
/// </summary>
public class GetTraceabilityPageQuery
{
    private readonly AppDbContext _db;

    public GetTraceabilityPageQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<TraceabilityPageVm> ExecuteAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        string[] requirementDocs = ["BRD.docx", "ProductBrief.docx"];

        var projects = await _db.ProjectDocuments.AsNoTracking()
            .Where(d => requirementDocs.Contains(d.FileName) && d.Content != "")
            .GroupBy(d => new { d.ProjectId, d.Project.Name })
            .Select(g => new { g.Key.ProjectId, g.Key.Name, Latest = g.Max(d => d.CreatedAt) })
            .OrderByDescending(x => x.Latest)
            .Select(x => new TraceabilityProjectOption(x.ProjectId, x.Name))
            .ToListAsync(cancellationToken);

        var selected = projects.FirstOrDefault(p => p.Id == projectId) ?? projects.FirstOrDefault();
        if (selected == null)
            return new TraceabilityPageVm(projects, null, null, null, null, null, 0, null, 0, 0, 0);

        var stored = await _db.ProjectTraceabilities.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == selected.Id, cancellationToken);

        // JSON hỏng (dữ liệu tay/di sản) ⇒ coi như chưa phân tích — bấm "Phân tích" sẽ ghi đè bản lành.
        var matrix = stored == null ? null : TraceabilityMatrixParser.Deserialize(stored.MatrixJson);

        return new TraceabilityPageVm(
            projects,
            selected.Id,
            selected.Name,
            matrix,
            matrix == null ? null : stored!.GeneratedAt,
            matrix == null ? null : stored!.ModelName,
            matrix == null ? 0 : stored!.TotalTokens,
            matrix == null ? null : stored!.GeneratedByUsername,
            matrix?.Requirements.Count(r => r.Status == TraceabilityMatrix.StatusCovered) ?? 0,
            matrix?.Requirements.Count(r => r.Status == TraceabilityMatrix.StatusPartial) ?? 0,
            matrix?.Requirements.Count(r => r.Status == TraceabilityMatrix.StatusMissing) ?? 0);
    }
}
