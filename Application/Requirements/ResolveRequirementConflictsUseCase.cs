using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ResolveConflictsResult { Ok, ProjectNotFound, NoResolutions, BaNotConfigured }

/// <summary>
/// Người dùng đã chốt lại từng điểm mâu thuẫn ở cổng soát: ghi lựa chọn vào hội thoại (một lượt user +
/// một lượt BA xác nhận) rồi gỡ cổng. Ghi vào chính hội thoại — không phải một bảng riêng — để mọi thứ
/// đọc transcript (soạn Brief, nhật ký điều đã chốt, bản đồ bao phủ) đều thấy phương án đã chốt mà không
/// cần biết cổng này tồn tại.
/// </summary>
public class ResolveRequirementConflictsUseCase
{
    private readonly AppDbContext _db;
    private readonly RequirementConflictService _conflicts;
    private readonly BAAgentResolver _agentResolver;
    private readonly BAConversationLog _conversationLog;

    public ResolveRequirementConflictsUseCase(
        AppDbContext db,
        RequirementConflictService conflicts,
        BAAgentResolver agentResolver,
        BAConversationLog conversationLog)
    {
        _db = db;
        _conflicts = conflicts;
        _agentResolver = agentResolver;
        _conversationLog = conversationLog;
    }

    public async Task<ResolveConflictsResult> ExecuteAsync(Guid projectId, IReadOnlyList<ConflictResolution> resolutions, CancellationToken cancellationToken = default)
    {
        var cleaned = (resolutions ?? Array.Empty<ConflictResolution>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Question) && !string.IsNullOrWhiteSpace(r.Choice))
            .Select(r => new ConflictResolution(r.Question.Trim(), r.Choice.Trim()))
            .ToList();

        if (cleaned.Count == 0)
            return ResolveConflictsResult.NoResolutions;

        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return ResolveConflictsResult.ProjectNotFound;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return ResolveConflictsResult.BaNotConfigured;

        await _conflicts.ApplyResolutionsAsync(project, ba, cleaned, _conversationLog, cancellationToken);
        return ResolveConflictsResult.Ok;
    }
}
