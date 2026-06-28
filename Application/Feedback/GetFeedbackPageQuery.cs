using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Feedback;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Feedback;

/// <summary>
/// Dựng dữ liệu màn hình Feedback. Người có quyền <see cref="AppPermission.FeedbackManage"/> thấy TOÀN BỘ
/// phản hồi; người dùng thường chỉ thấy phản hồi của chính mình (theo username). Hỗ trợ lọc theo trạng thái
/// và loại. Trả kèm giới hạn upload để view hiển thị/hint.
/// </summary>
public class GetFeedbackPageQuery
{
    private readonly AppDbContext _db;
    private readonly FeedbackAttachmentStore _store;

    public GetFeedbackPageQuery(AppDbContext db, FeedbackAttachmentStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task<FeedbackPage> ExecuteAsync(
        string? username, bool canManage, FeedbackStatus? statusFilter, FeedbackType? typeFilter,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Feedbacks
            .AsNoTracking()
            .Include(f => f.Attachments)
            .AsQueryable();

        // Người dùng thường: chỉ phản hồi của mình. Không có username (hiếm) ⇒ không thấy gì.
        if (!canManage)
            query = query.Where(f => f.SubmittedByUsername != null && f.SubmittedByUsername == username);

        if (statusFilter.HasValue)
            query = query.Where(f => f.Status == statusFilter.Value);
        if (typeFilter.HasValue)
            query = query.Where(f => f.Type == typeFilter.Value);

        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(cancellationToken);

        var items = rows.Select(f => new FeedbackListItem(
            f.Id,
            f.Type,
            f.Status,
            f.Title,
            f.Message,
            string.IsNullOrWhiteSpace(f.SubmittedByName) ? (f.SubmittedByUsername ?? "—") : f.SubmittedByName,
            f.CreatedAt,
            username != null && f.SubmittedByUsername == username,
            f.Attachments
                .OrderBy(a => a.CreatedAt)
                .Select(a => new FeedbackAttachmentVm(a.Id, a.FileName, a.Kind, a.ContentType, a.SizeBytes))
                .ToList()))
            .ToList();

        return new FeedbackPage(
            items,
            canManage,
            statusFilter,
            typeFilter,
            items.Count,
            _store.MaxFileBytes,
            _store.MaxFiles,
            FeedbackAttachmentStore.AllowedExtensionList.OrderBy(x => x).ToList());
    }
}
