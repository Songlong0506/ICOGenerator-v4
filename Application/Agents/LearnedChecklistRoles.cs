using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;

namespace ICOGenerator.Application.Agents;

/// <summary>
/// Những vai CÓ checklist học được — tức có cả đường GHI (một vòng harvest bồi bài học) lẫn đường ĐỌC
/// (bài học được nạp lại vào prompt của vai). Màn hình Agents dùng danh sách này để chỉ hiện nút checklist
/// ở đúng các vai đó: một trang checklist cho vai không có đường ghi nào sẽ rỗng vĩnh viễn, và nút dẫn tới
/// nó chỉ làm người dùng đi tìm thứ không tồn tại.
///
/// <para>
/// SUY từ <see cref="DeliveryPipeline.Steps"/> chứ không liệt kê tay: mỗi bước pipeline là một cổng duyệt,
/// và nhận xét ở cổng đó thành bài học cho vai chạy bước (<c>StageRevisionMemoryService</c>). Thêm một
/// bước cho vai mới ⇒ vai đó tự có checklist, không phải nhớ sửa thêm chỗ này. Business Analyst luôn có
/// mặt vì ba đường học từ buổi phỏng vấn không đi qua pipeline.
/// </para>
/// </summary>
public static class LearnedChecklistRoles
{
    public static readonly IReadOnlyList<AgentRoleKey> All = DeliveryPipeline.Steps
        .Select(s => s.Role)
        .Append(AgentRoleKey.BusinessAnalyst)
        .Distinct()
        .OrderBy(r => r)
        .ToList();

    public static bool Has(AgentRoleKey role) => All.Contains(role);
}
