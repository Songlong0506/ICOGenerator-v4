using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Cầu nối giữa danh tính SSO (IdentityServer) và mô hình người dùng của app. Toàn bộ phân quyền của
/// app chạy theo bảng AppUser (claim Role) và quyền sở hữu gắn theo username, nên sau khi IdentityServer
/// xác thực xong ta phải quy về một AppUser: tra theo username lấy từ token, tự tạo mới khi được phép,
/// hoặc từ chối. Trả về AppUser để bên gọi phát claim Name/Role chuẩn của app.
/// </summary>
public class SsoUserProvisioner
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;
    private readonly ILogger<SsoUserProvisioner> _logger;

    public SsoUserProvisioner(AppDbContext db, IPasswordHasher<AppUser> passwordHasher, ILogger<SsoUserProvisioner> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    /// <summary>
    /// Quy danh tính SSO về một AppUser. Trả về null khi phải TỪ CHỐI truy cập: user tồn tại nhưng bị
    /// khóa (IsActive = false), hoặc chưa có mà <paramref name="autoProvision"/> = false.
    /// </summary>
    public async Task<AppUser?> ResolveOrProvisionAsync(
        string username,
        string? displayName,
        string? email,
        UserRole defaultRole,
        CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim();
        if (normalized.Length == 0)
            return null;

        // So khớp không phân biệt hoa/thường: NTID/email có thể tới ở nhiều kiểu chữ, còn Sqlite (dùng khi
        // chạy end-to-end không có SQL Server) mặc định phân biệt hoa/thường khác với SQL Server.
        var lowered = normalized.ToLower();
        var user = await _db.AppUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == lowered, cancellationToken);

        if (user is not null)
            return user.IsActive ? user : null;

        var created = new AppUser
        {
            Username = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName!.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim(),
            Role = defaultRole,
            IsActive = true
        };
        // User SSO không có mật khẩu cục bộ: đặt hash ngẫu nhiên (không thể đăng nhập bằng form Local).
        created.PasswordHash = _passwordHasher.HashPassword(created, Guid.NewGuid().ToString("N"));

        _db.AppUsers.Add(created);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tự tạo AppUser cho SSO user {Username} với vai trò {Role}.", normalized, defaultRole);
        return created;
    }
}
