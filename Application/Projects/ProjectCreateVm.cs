using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

// End-user (không rành kỹ thuật) chỉ nhập Name + Description khi tạo project. Các field kỹ thuật
// (Generation Mode, Backend/Frontend Git) do TeamDev điền sau ở Agent Dashboard — xem UpdateDeliveryConfigVm.
public class ProjectCreateVm
{
    [Required] public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    // Đơn vị yêu cầu (tùy chọn) — mã OrgUnits.OrgUnitCode chọn từ dropdown; rỗng = chưa gắn.
    [MaxLength(50)] public string? OrgUnitCode { get; set; }
}
