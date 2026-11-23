namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt chắt lọc PHẠM VI (<c>InterviewScopeService</c>): các TÁC VỤ vừa lộ ra trong hội thoại
/// SAU lần thiết kế UX gần nhất mà danh mục tác vụ chưa có (một thao tác mới, một trạng thái mới, một danh
/// mục mới cần quản lý). Chúng vào <c>Project.UxBlueprint</c> ở trạng thái CHƯA XẾP CHỖ, và lượt thiết kế kế
/// tiếp (<c>UxDesignService</c>) đặt chúng vào màn hình.
///
/// <para>
/// <b>Chỉ chạy SAU lần thiết kế đầu.</b> Lần thiết kế đầu tự đọc trọn hội thoại (<c>UxDesignService</c> dựng
/// danh mục tác vụ từ bảng luồng + bảng báo cáo + hội thoại), nên không cần lượt chắt nào đứng trước nó. Sau
/// đó lượt này chạy theo LÔ (<c>InterviewScopeService.HarvestBatchThreshold</c>) để bắt phần phạm vi trôi tiếp mà
/// không phải dựng lại cả danh mục ở mỗi lượt chat.
/// </para>
///
/// <para>
/// Thứ được chắt là TÁC VỤ, không phải màn hình: cần bao nhiêu màn hình và tác vụ mới đặt ở đâu là việc
/// THIẾT KẾ của lượt thiết kế UX — lượt chắt lọc chỉ nói "người dùng vừa kể thêm việc gì".
/// </para>
/// </summary>
public class InterviewScope
{
    public List<UxTaskDraft> TaskAdditions { get; set; } = new();
}

