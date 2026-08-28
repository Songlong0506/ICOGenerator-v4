using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

// End-user (không rành kỹ thuật) chỉ nhập Name + Description + đơn vị yêu cầu khi tạo project. Các field
// kỹ thuật (Generation Mode, Backend/Frontend Git) do TeamDev điền sau ở Agent Dashboard — xem
// UpdateDeliveryConfigVm.
public class ProjectCreateVm
{
    [Required] public string Name { get; set; } = string.Empty;
    // Nullable CÓ CHỦ Ý: mô tả là field tùy chọn, mà kiểu `string` không-nullable trong ngữ cảnh
    // nullable-enable bị MVC tự gắn [Required] ngầm ⇒ để trống ô Description là ModelState invalid và
    // không tạo được project. Nhận null/rỗng rồi tự quy về chuỗi rỗng ở use case.
    [MaxLength(2000)] public string? Description { get; set; }
    // Đơn vị yêu cầu (BẮT BUỘC) — mã OrgUnits.OrgUnitCode chọn từ dropdown. Bắt buộc ngay từ lúc tạo vì
    // toàn bộ luồng BA/tài liệu/Usage đều dựa vào nó (xem docs/requirement-flow.md — BuildProjectUnitNoteAsync):
    // project không có đơn vị thì BA mất bối cảnh phòng ban và Usage không roll-up được theo department.
    [Required] [MaxLength(50)] public string? OrgUnitCode { get; set; }
}
