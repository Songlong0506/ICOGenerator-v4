using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Một vòng "Yêu cầu chỉnh sửa" POC đã hoàn tất: bàn giao cuối của agent chính là changelog đối chiếu từng ghi chú.</summary>
public record PocRevisionEntry(string Title, DateTime? FinishedAt, string Output);

public record PocReviewPage(
    Guid ProjectId,
    string ProjectName,
    bool HasMockup,
    UatScenarioSet Scenarios,
    IReadOnlyList<PocRevisionEntry> Revisions);

/// <summary>
/// Dữ liệu cho trang review POC (Projects/PocReview): tên project, POC đã tồn tại chưa, bộ kịch bản
/// UAT (checklist đi từng bước, sinh sau khi POC dựng xong — xem <see cref="UatScenarioService"/>) và
/// changelog các vòng chỉnh sửa đã chạy (bàn giao của agent, để người review thấy NHỮNG GÌ ĐÃ ĐỔI sau
/// khi gửi ghi chú thay vì phải tự so hai bản POC).
/// </summary>
public class GetPocReviewQuery
{
    private const int MaxRevisionEntries = 5;

    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly UatScenarioService _uatScenarios;

    public GetPocReviewQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver, UatScenarioService uatScenarios)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _uatScenarios = uatScenarios;
    }

    public async Task<PocReviewPage?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var mockupPath = _workspacePathResolver.GetMockupPath(
            WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        var scenarios = await _uatScenarios.LoadAsync(project.Id, project.Name, cancellationToken);

        // Các vòng chỉnh sửa POC đã xong, mới nhất trước. Prompt revision yêu cầu bàn giao cuối nêu rõ
        // đã đổi gì ứng với từng ghi chú — hiển thị nguyên văn làm changelog.
        var revisions = await _db.AgentTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId
                        && t.Type == AgentTaskType.PocPreview
                        && t.RevisionFeedback != null
                        && t.Status == AgentTaskStatus.Completed
                        && t.Output != null && t.Output != "")
            .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
            .Take(MaxRevisionEntries)
            .Select(t => new PocRevisionEntry(t.Title, t.FinishedAt, t.Output!))
            .ToListAsync(cancellationToken);

        return new PocReviewPage(project.Id, project.Name, File.Exists(mockupPath), scenarios, revisions);
    }
}
