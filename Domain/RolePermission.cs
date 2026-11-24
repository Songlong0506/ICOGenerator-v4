using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một dòng cấp quyền: role <see cref="Role"/> được phép thực hiện <see cref="Permission"/>.
/// Bảng này là nguồn dữ liệu CẤU HÌNH được — admin chỉnh qua popup Roles &amp; Permissions.
/// <see cref="Role"/> là tên role trên IdentityServer đã chuẩn hoá (xem <see cref="Security.RoleNames"/>),
/// không phải enum: role mới tạo bên IS4 cấp quyền được ngay, không cần sửa code.
/// Role Admin luôn có thêm quyền quản lý ma trận dù bảng không có dòng nào (xem PermissionService).
/// </summary>
public class RolePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = string.Empty;
    public AppPermission Permission { get; set; }
}
