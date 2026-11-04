using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Task <c>PocPreview</c> đã DỰNG MỚI bản demo chính đang được review — điểm xuất phát chung của mọi biến
/// thể thử model (cùng AI Design Spec ⇒ cùng đầu vào). Bỏ các vòng "Yêu cầu chỉnh sửa": vòng đó vá trên bản
/// có sẵn, không phải một lượt dựng từ đầu nên không so ngang được với một biến thể.
/// </summary>
internal static class PocVariantSource
{
    public static Task<AgentTask?> FindAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        db.AgentTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId
                        && t.Type == AgentTaskType.PocPreview
                        && t.RevisionFeedback == null
                        && t.Status == AgentTaskStatus.Completed)
            .OrderByDescending(t => t.FinishedAt ?? t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
