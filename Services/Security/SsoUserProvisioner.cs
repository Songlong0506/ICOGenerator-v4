using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Cầu nối giữa danh tính SSO (IdentityServer) và mô hình người dùng của app. Toàn bộ phân quyền của
/// app chạy theo vai trò của AppUser (phát ra thành các claim Role) và quyền sở hữu gắn theo username,
/// nên sau khi IdentityServer xác thực xong ta phải quy về một AppUser: tra theo username lấy từ token,
/// tự tạo mới khi được phép, hoặc từ chối. Trả về AppUser (kèm tập vai trò) để bên gọi phát claim
/// Name/Role chuẩn của app.
/// </summary>
public class SsoUserProvisioner
{
    /// <summary>
    /// Vai trò cho user MỚI khi role claim không khớp mapping nào. Chọn vai trò thấp nhất: tự cấp quyền
    /// cho người lạ nguy hiểm hơn nhiều so với việc họ phải xin cấp thêm.
    /// </summary>
    private const UserRole DefaultRole = UserRole.User;

    private readonly AppDbContext _db;
    private readonly ILogger<SsoUserProvisioner> _logger;

    public SsoUserProvisioner(AppDbContext db, ILogger<SsoUserProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Quy danh tính SSO về một AppUser. Vai trò lấy từ Claims (<paramref name="rolesFromClaims"/>):
    /// user MỚI được tạo với TẬP vai trò này (rơi về <see cref="DefaultRole"/> nếu Claims không ánh xạ
    /// được vai trò nào), user ĐÃ TỒN TẠI được ĐỒNG BỘ tập vai trò + đơn vị tổ chức từ Claims. Trả về
    /// null khi phải TỪ CHỐI truy cập: username rỗng.
    /// </summary>
    /// <param name="rolesFromClaims">Tập vai trò ánh xạ từ role claim của IdentityServer (xem
    /// <c>IdentityServerSettings.MapRoles</c>). RỖNG = Claims không khớp mapping nào ⇒ user mới dùng
    /// <see cref="DefaultRole"/>, user cũ GIỮ NGUYÊN tập vai trò (tránh vô tình hạ quyền khi mapping chưa
    /// cấu hình đủ). KHÔNG rỗng ⇒ IdentityServer là nguồn sự thật: vai trò thừa bị THU HỒI, vai trò mới
    /// được cấp thêm.</param>
    /// <param name="orgUnitName">Đơn vị tổ chức từ claim "department"; đồng bộ lại mỗi lần đăng nhập khi
    /// có giá trị. Trống ⇒ GIỮ NGUYÊN giá trị cũ (không xóa khi IdP tạm thời không phát claim).</param>
    public async Task<AppUser?> ResolveOrProvisionAsync(
        string? username,
        string? displayName,
        string? email,
        IReadOnlyCollection<UserRole> rolesFromClaims,
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
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Username.ToLower() == lowered, cancellationToken);

        var trimmedOrgUnit = string.IsNullOrWhiteSpace(orgUnitName) ? null : orgUnitName!.Trim();

        if (user is not null)
        {
            var changed = SyncRoles(user, rolesFromClaims);

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

        var initialRoles = rolesFromClaims.Count > 0 ? rolesFromClaims.Distinct() : new[] { DefaultRole }.AsEnumerable();
        var created = new AppUser
        {
            Username = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName!.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim(),
            OrgUnitName = trimmedOrgUnit
        };
        foreach (var role in initialRoles)
            created.Roles.Add(new AppUserRole { Role = role });

        _db.AppUsers.Add(created);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tự tạo AppUser cho SSO user {Username} với vai trò {Roles}.",
            normalized, Describe(created.Roles));
        return created;
    }

    /// <summary>
    /// Đưa tập vai trò của user về đúng tập từ Claims: thu hồi vai trò không còn, cấp thêm vai trò mới.
    /// Claims RỖNG ⇒ không đụng gì (giữ nguyên vai trò cũ — xem chú thích tham số rolesFromClaims).
    /// Trả về true nếu có thay đổi cần lưu.
    /// </summary>
    private bool SyncRoles(AppUser user, IReadOnlyCollection<UserRole> rolesFromClaims)
    {
        if (rolesFromClaims.Count == 0)
            return false;

        var target = rolesFromClaims.ToHashSet();
        var current = user.Roles.Select(r => r.Role).ToHashSet();
        if (target.SetEquals(current))
            return false;

        var before = Describe(user.Roles);

        foreach (var revoked in user.Roles.Where(r => !target.Contains(r.Role)).ToList())
            user.Roles.Remove(revoked);
        foreach (var added in target.Where(r => !current.Contains(r)))
            user.Roles.Add(new AppUserRole { AppUserId = user.Id, Role = added });

        _logger.LogInformation(
            "Đồng bộ vai trò SSO user {Username}: {OldRoles} → {NewRoles}.",
            user.Username, before, Describe(user.Roles));
        return true;
    }

    private static string Describe(IEnumerable<AppUserRole> roles)
    {
        var names = roles.Select(r => r.Role.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? "(không có)" : string.Join(", ", names);
    }
}
