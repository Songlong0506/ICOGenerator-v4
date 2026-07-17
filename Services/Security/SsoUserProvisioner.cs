using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
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
    private readonly ILogger<SsoUserProvisioner> _logger;

    public SsoUserProvisioner(AppDbContext db, ILogger<SsoUserProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Quy danh tính SSO về một AppUser. Vai trò lấy từ Claims (<paramref name="roleFromClaims"/>):
    /// user MỚI được tạo với vai trò này (rơi về <paramref name="defaultRole"/> nếu Claims không ánh xạ
    /// được vai trò nào), user ĐÃ TỒN TẠI được ĐỒNG BỘ vai trò + đơn vị tổ chức từ Claims. Trả về null
    /// khi phải TỪ CHỐI truy cập: username rỗng.
    /// </summary>
    /// <param name="roleFromClaims">Vai trò ánh xạ từ role claim của IdentityServer; null = Claims không
    /// khớp mapping nào ⇒ user mới dùng <paramref name="defaultRole"/>, user cũ GIỮ NGUYÊN vai trò (tránh
    /// vô tình hạ quyền khi mapping chưa cấu hình đủ).</param>
    /// <param name="orgUnitName">Đơn vị tổ chức từ claim "department"; đồng bộ lại mỗi lần đăng nhập khi
    /// có giá trị. Trống ⇒ GIỮ NGUYÊN giá trị cũ (không xóa khi IdP tạm thời không phát claim).</param>
    public async Task<AppUser?> ResolveOrProvisionAsync(
        string? username,
        string? displayName,
        string? email,
        UserRole? roleFromClaims,
        string? orgUnitName = null,
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

        var trimmedOrgUnit = string.IsNullOrWhiteSpace(orgUnitName) ? null : orgUnitName!.Trim();

        if (user is not null)
        {
            var changed = false;

            // Đồng bộ vai trò từ Claims: IdentityServer là nguồn sự thật về vai trò. Chỉ ghi DB khi ánh xạ
            // được vai trò từ claim VÀ khác vai trò hiện tại (claim không khớp mapping ⇒ giữ nguyên).
            if (roleFromClaims is UserRole role && user.Role != role)
            {
                _logger.LogInformation(
                    "Đồng bộ vai trò SSO user {Username}: {OldRole} → {NewRole}.", user.Username, user.Role, role);
                user.Role = role;
                changed = true;
            }

            // Đồng bộ đơn vị tổ chức từ claim department. Chỉ cập nhật khi claim CÓ giá trị và khác giá trị
            // hiện tại; claim trống ⇒ giữ nguyên (tránh xóa khi IdP tạm thời không phát department).
            if (trimmedOrgUnit is not null && user.OrgUnitName != trimmedOrgUnit)
            {
                user.OrgUnitName = trimmedOrgUnit;
                changed = true;
            }

            if (changed)
                await _db.SaveChangesAsync(cancellationToken);

            return user;
        }

        var created = new AppUser
        {
            Username = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName!.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim(),
            Role = roleFromClaims ?? UserRole.User,
            OrgUnitName = trimmedOrgUnit
        };

        _db.AppUsers.Add(created);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tự tạo AppUser cho SSO user {Username} với vai trò {Role}.", normalized, roleFromClaims);
        return created;
    }
}
