using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ReopenCoverageResult { Ok, ProjectNotFound, GroupNotFound }

/// <summary>
/// "Chỗ này chưa đúng" trên một nhóm của bản đồ bao phủ: hạ nhóm đó xuống [MỘT PHẦN] để BA hỏi lại và
/// cổng "Write Requirement" đóng lại cho tới khi nó được chốt lại.
///
/// Đây là đường thoát khỏi điểm mù duy nhất của bản đồ: nhóm bị chấm [RÕ] oan sẽ không bao giờ được BA
/// nhắc lại (prompt cấm hỏi lại nhóm đã [RÕ]), nên nếu người dùng không có nút này thì cách hiểu sai đi
/// thẳng vào tài liệu mà không cổng nào chặn.
/// </summary>
public class ReopenCoverageGroupUseCase
{
    private readonly AppDbContext _db;

    public ReopenCoverageGroupUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(ReopenCoverageResult Result, IReadOnlyList<CoverageMapItem> Coverage, bool Ready)> ExecuteAsync(
        Guid projectId, string label, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return (ReopenCoverageResult.ProjectNotFound, Array.Empty<CoverageMapItem>(), false);

        var updated = CoverageMapEditor.Reopen(project.RequirementCoverageMap, label);
        if (updated == null)
            return (ReopenCoverageResult.GroupNotFound, CoverageMapParser.Parse(project.RequirementCoverageMap).ToList(),
                RequirementReadinessGate.Evaluate(project.RequirementCoverageMap).Ready);

        project.RequirementCoverageMap = updated;
        await _db.SaveChangesAsync(cancellationToken);

        return (ReopenCoverageResult.Ok,
            CoverageMapParser.Parse(updated).ToList(),
            RequirementReadinessGate.Evaluate(updated).Ready);
    }
}
