using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Loại đối tượng cấu hình bị thay đổi, dùng để gom nhóm và lọc trong trang Audit Log. Lưu xuống DB dạng
/// chuỗi (tên enum) nên ĐỪNG đổi tên các giá trị đã ghi (sẽ làm "mồ côi" các bản ghi log cũ).
/// </summary>
public enum AuditCategory
{
    [Description("Settings")]
    Settings,
    [Description("Role & Permissions")]
    Role,
    [Description("Agent")]
    Agent,
    [Description("AI Model")]
    Model
}
