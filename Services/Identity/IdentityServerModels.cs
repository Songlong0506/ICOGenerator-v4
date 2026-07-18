namespace ICOGenerator.Services.Identity;

/// <summary>Role của một API resource trên Bosch IdentityServer (kết quả <c>GetAllRoles</c>).</summary>
public class IdentityServerRoleResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Người dùng LDAP trả về từ IdentityServer (tra cứu theo tên hoặc theo role). Giữ nguyên tập
/// trường như API Bosch để deserialize không phân biệt hoa/thường vẫn map đủ.
/// </summary>
public class LdapUserResponse
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayNameDetail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GivenName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public string Surname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string OrganizationUnit { get; set; } = string.Empty;
    public string PersonalNumber { get; set; } = string.Empty;
}

/// <summary>Tham số lọc người dùng theo role (POST <c>IdentityServerUser</c>).</summary>
public class UserByRoleRequest
{
    public string Key { get; set; } = string.Empty;
    public string[] OrganizeKeys { get; set; } = Array.Empty<string>();
    public string[] RoleKeys { get; set; } = Array.Empty<string>();
}

/// <summary>Payload gán (<c>RoleForUser</c>) / thu hồi (<c>WithdrawalRole</c>) role cho một user.</summary>
public class AssignRoleRequest
{
    /// <summary>Tên API resource sở hữu role (vd HCP_CBO_API) — lấy từ cấu hình IdentityServer:APIName.</summary>
    public string ApiResource { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string[] RoleNames { get; set; } = Array.Empty<string>();
}
