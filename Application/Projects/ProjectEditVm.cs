using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

// Form "Chỉnh sửa dự án" ở trang Projects — đúng ba field người dùng nhập lúc tạo (xem ProjectCreateVm).
// Các field kỹ thuật (Generation Mode, Backend/Frontend Git) KHÔNG nằm ở đây: chúng thuộc cấu hình
// delivery do TeamDev sửa ở Agent Dashboard (UpdateDeliveryConfigVm) — mỗi màn hình sửa đúng phần của mình.
public class ProjectEditVm
{
    public Guid ProjectId { get; set; }
    [Required] [MaxLength(200)] public string Name { get; set; } = string.Empty;
    // Nullable CÓ CHỦ Ý, cùng lý do với ProjectCreateVm: mô tả là field tùy chọn, mà kiểu `string`
    // không-nullable bị MVC gắn [Required] ngầm ⇒ XOÁ TRẮNG ô Description là ModelState invalid và lần
    // lưu bị từ chối. UpdateProjectUseCase tự quy null về chuỗi rỗng.
    [MaxLength(2000)] public string? Description { get; set; }
    // Đơn vị yêu cầu — mã OrgUnits.OrgUnitCode chọn từ dropdown; rỗng = bỏ gắn đơn vị.
    [MaxLength(50)] public string? OrgUnitCode { get; set; }
}
