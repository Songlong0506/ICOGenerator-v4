using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Quality;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Quality;

public record BuildTraceabilityResponse(bool Ok, string? Error);

/// <summary>
/// Chạy phân tích ma trận truy vết cho một project (một lời gọi LLM — xem TraceabilityMatrixBuilder)
/// rồi UPSERT kết quả vào ProjectTraceability (mỗi project một dòng, bản mới ghi đè bản cũ). Thao tác
/// đồng bộ: UI fetch chờ như chat BA; lỗi trả thông điệp đọc được thay vì ném exception.
/// </summary>
public class BuildTraceabilityMatrixUseCase
{
    private readonly AppDbContext _db;
    private readonly TraceabilityMatrixBuilder _builder;

    public BuildTraceabilityMatrixUseCase(AppDbContext db, TraceabilityMatrixBuilder builder)
    {
        _db = db;
        _builder = builder;
    }

    public async Task<BuildTraceabilityResponse> ExecuteAsync(Guid projectId, string? username, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return new BuildTraceabilityResponse(false, "Project không tồn tại.");

        var outcome = await _builder.BuildAsync(project, cancellationToken);
        if (!outcome.IsSuccess)
            return new BuildTraceabilityResponse(false, outcome.Error);

        var stored = await _db.ProjectTraceabilities.FirstOrDefaultAsync(x => x.ProjectId == projectId, cancellationToken);
        if (stored == null)
        {
            stored = new ProjectTraceability { ProjectId = projectId };
            _db.ProjectTraceabilities.Add(stored);
        }

        stored.MatrixJson = outcome.MatrixJson!;
        stored.ModelName = outcome.ModelName;
        stored.TotalTokens = outcome.TotalTokens;
        stored.GeneratedByUsername = username;
        stored.GeneratedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new BuildTraceabilityResponse(true, null);
    }
}
