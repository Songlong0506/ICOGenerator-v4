using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Feedback;

/// <summary>File đính kèm đã giải quyền, sẵn sàng trả về cho client.</summary>
public record FeedbackAttachmentDownload(string StoredPath, string ContentType, string FileName);

/// <summary>
/// Lấy một file đính kèm để xem/tải. Kiểm soát truy cập: người có quyền FeedbackManage tải được mọi file;
/// người dùng thường chỉ tải file của phản hồi DO MÌNH gửi. Không đủ quyền hoặc không tồn tại ⇒ trả null
/// (controller trả 404 — không phân biệt "không có" với "không được phép" để tránh lộ sự tồn tại).
/// </summary>
public class GetFeedbackAttachmentQuery
{
    private readonly AppDbContext _db;

    public GetFeedbackAttachmentQuery(AppDbContext db) => _db = db;

    public async Task<FeedbackAttachmentDownload?> ExecuteAsync(
        Guid attachmentId, string? username, bool canManage, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.FeedbackAttachments
            .AsNoTracking()
            .Include(a => a.Feedback)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);

        if (attachment == null)
            return null;

        var isOwner = username != null && attachment.Feedback.CreatedByUsername == username;
        if (!canManage && !isOwner)
            return null;

        if (!File.Exists(attachment.StoredPath))
            return null;

        return new FeedbackAttachmentDownload(attachment.StoredPath, attachment.ContentType, attachment.FileName);
    }
}
