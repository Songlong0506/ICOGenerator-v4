using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

// Form "Chỉnh sửa dự án" ở trang Projects — đúng ba field người dùng nhập lúc tạo (xem ProjectCreateVm).
// Các field kỹ thuật (Generation Mode, Backend/Frontend Git) KHÔNG nằm ở đây: chúng thuộc cấu hình
// delivery do TeamDev sửa ở Agent Dashboard (UpdateDeliveryConfigVm) — mỗi màn hình sửa đúng phần của mình.
public class ProjectEditVm
{
    public Guid ProjectId { get; set; }
    [Required] [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(2000)] public string Description { get; set; } = string.Empty;
    // Đơn vị yêu cầu — mã OrgUnits.OrgUnitCode chọn từ dropdown; rỗng = bỏ gắn đơn vị.
    [MaxLength(50)] public string? OrgUnitCode { get; set; }
}
