using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Đường thứ ba của ghi chú POC: góp ý về THIẾT KẾ màn hình ("gộp hai màn này", "nút Duyệt nên ở trang chi
/// tiết", "danh mục để chung một màn có tab") đi thẳng vào bản thiết kế UX thay vì vào hội thoại BA.
///
/// <para>
/// <b>Vì sao không gửi chúng về Requirement như trước.</b> Đường Requirement soạn ghi chú thành một tin nhắn
/// NGƯỜI DÙNG trong hội thoại rồi soạn lại Brief — nhưng góp ý thiết kế không đổi nghiệp vụ nào, và bản thiết
/// kế được dựng bằng một lượt riêng chỉ mở lại khi phạm vi trôi. Câu "gộp hai màn" nằm trong hội thoại thì
/// không cổng nào mở lại lượt thiết kế, và Brief soạn lại vẫn liệt kê đúng bộ màn cũ. Ở đây góp ý được lưu
/// thành <see cref="UxBlueprint.DesignNotes"/> (sống qua mọi lần thiết kế sau), bản thiết kế được dựng lại ở
/// chế độ <c>feedback</c>, rồi Brief soạn lại theo bản mới — người dùng duyệt lại, spec và POC dựng lại.
/// </para>
///
/// <para>
/// Hỏng ở bước thiết kế ⇒ dừng hẳn, KHÔNG đổi trạng thái ghi chú nào (bấm lại được). Ghi chú đi đường này
/// mang trạng thái <see cref="PocCommentStatus.RoutedToRequirement"/> như đường tài liệu — cả hai cùng đóng khi
/// bản demo dựng lại (<c>PocDeferredFeedbackService.MarkRoutedAppliedAsync</c>) — và route
/// <see cref="PocCommentRoute.Design"/> để phân biệt.
/// </para>
/// </summary>
public class RoutePocFeedbackToDesignUseCase
{
    private readonly AppDbContext _db;
    private readonly BAAgentResolver _agentResolver;
    private readonly UxDesignService _uxDesign;
    private readonly GenerateRequirementDraftUseCase _generateDraft;

    public RoutePocFeedbackToDesignUseCase(
        AppDbContext db,
        BAAgentResolver agentResolver,
        UxDesignService uxDesign,
        GenerateRequirementDraftUseCase generateDraft)
    {
        _db = db;
        _agentResolver = agentResolver;
        _uxDesign = uxDesign;
        _generateDraft = generateDraft;
    }

    /// <param name="regenerateDraft">
    /// false khi cùng lượt còn đường Requirement chạy ngay sau (nó tự soạn lại Brief) — soạn hai lần là phí một
    /// lời gọi dài nhất của phía yêu cầu.
    /// </param>
    public async Task<RoutePocFeedbackResult> ExecuteAsync(
        Guid projectId, IReadOnlyCollection<Guid> commentIds, bool regenerateDraft = true, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return RoutePocFeedbackResult.ProjectNotFound;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return RoutePocFeedbackResult.BaNotConfigured;

        var ids = commentIds.Distinct().ToList();
        var selected = await _db.PocComments
            .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Open && ids.Contains(c.Id))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        if (selected.Count == 0)
            return RoutePocFeedbackResult.NoOpenComments;

        var notes = selected.Select(c => new UxDesignNote
        {
            Screen = (c.PageView ?? string.Empty).Trim(),
            Text = string.IsNullOrWhiteSpace(c.ElementLabel)
                ? c.Comment.Trim()
                : $"{c.ElementLabel.Trim()}: {c.Comment.Trim()}",
            CreatedAt = DateTime.UtcNow
        }).ToList();

        var redesigned = await _uxDesign.RedesignFromFeedbackAsync(project, ba, notes, cancellationToken);
        if (!redesigned)
            return RoutePocFeedbackResult.DesignFailed;

        foreach (var c in selected)
        {
            c.Status = PocCommentStatus.RoutedToRequirement;
            c.Route = PocCommentRoute.Design;
        }
        await _db.SaveChangesAsync(cancellationToken);

        if (regenerateDraft)
            await _generateDraft.ExecuteAsync(projectId);
        return RoutePocFeedbackResult.Ok;
    }
}
