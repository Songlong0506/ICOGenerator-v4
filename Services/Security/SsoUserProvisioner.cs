using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Cầu nối giữa danh tính SSO (IdentityServer) và mô hình người dùng của app. Quyền sở hữu (project, thông
/// báo, trí nhớ người dùng) gắn theo username, nên sau khi IdentityServer xác thực xong ta phải quy về một
/// AppUser: tra theo username lấy từ token, tự tạo mới khi được phép, hoặc từ chối. Chế độ đăng nhập cục bộ
/// (IdentityServer:LocalRole có giá trị) cũng đi qua đây với username = tên vai trò, nên DB không cần seed user.
/// KHÔNG đụng tới vai trò: vai trò không được lưu ở đâu trong DB, bên gọi phát claim Role thẳng từ role
/// claim của IdP (xem <c>ApplicationServiceCollectionExtensions.BridgeSsoIdentityAsync</c>). Ở đây chỉ đồng
/// bộ những gì SỐNG LÂU HƠN một phiên: danh tính hiển thị, email.
/// </summary>
public class SsoUserProvisioner
{
    private readonly AppDbContext _db;
    private readonly ILogger<SsoUserProvisioner> _logger;

    public SsoUserProvisioner(AppDbContext db, ILogger<SsoUserProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Quy danh tính SSO về một AppUser: tra theo username, tạo mới nếu chưa có, đồng bộ đơn vị tổ chức.
    /// KHÔNG nhận và KHÔNG ghi vai trò — vai trò đi thẳng từ role claim vào claim của phiên. Trả về null
    /// khi phải TỪ CHỐI truy cập: username rỗng.
    /// </summary>
    public async Task<AppUser?> ResolveOrProvisionAsync(
        string? username,
        string? displayName,
        string? email,
        CancellationToken cancellationToken = default)
    {
        // Thiếu claim username (null/rỗng) ⇒ không xác định được AppUser ⇒ TỪ CHỐI.
        var normalized = username?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;

        // So khớp không phân biệt hoa/thường: NTID/email có thể tới ở nhiều kiểu chữ, còn Sqlite (dùng khi
        // chạy end-to-end không có SQL Server) mặc định phân biệt hoa/thường khác với SQL Server.
        var lowered = normalized.ToLower();
        var user = await _db.AppUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == lowered, cancellationToken);

        if (user is not null)
            return user;

        var created = new AppUser
        {
            Username = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName!.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim()
        };
        _db.AppUsers.Add(created);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Auto-created AppUser for SSO user {Username}.", normalized);
        return created;
    }
}
