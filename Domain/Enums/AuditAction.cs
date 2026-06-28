using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Loại thao tác ghi lại trong audit log. Lưu xuống DB dạng chuỗi (tên enum) nên ĐỪNG đổi tên giá trị đã ghi.
/// </summary>
public enum AuditAction
{
    [Description("Tạo mới")]
    Create,
    [Description("Cập nhật")]
    Update,
    [Description("Xóa")]
    Delete
}
