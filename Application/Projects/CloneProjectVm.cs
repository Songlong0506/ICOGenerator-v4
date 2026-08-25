using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Application.Projects;

// Form "Nhân bản dự án" ở trang Projects. Chỉ hỏi hai thứ người dùng thực sự phải quyết: tên bản sao và
// chép tới đâu. Mọi thứ còn lại (mô tả, đơn vị yêu cầu, cấu hình delivery) chép từ dự án gốc — sửa lại
// được ngay tại chỗ bằng form Chỉnh sửa nếu cần.
public class CloneProjectVm
{
    public Guid ProjectId { get; set; }

    // Rỗng ⇒ use case tự đặt "{tên gốc} (bản sao)". Cùng trần 200 ký tự với Project.Name.
    [MaxLength(200)] public string? Name { get; set; }

    public ProjectCloneScope Scope { get; set; } = ProjectCloneScope.Full;
}
