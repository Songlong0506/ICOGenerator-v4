using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Ghi một dấu vết thay đổi cấu hình vào bảng AuditLog. Tự lấy người thực hiện từ ngữ cảnh request hiện tại
/// và tự che (redact) các trường nhạy cảm trong <c>before</c>/<c>after</c> trước khi
/// lưu. KHÔNG bao giờ ném ra ngoài: một sự cố ghi log không được làm hỏng thao tác cấu hình đã thành công.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        AuditCategory category,
        AuditAction action,
        string entityId,
        string summary,
        object? before = null,
        object? after = null,
        CancellationToken cancellationToken = default);
}
