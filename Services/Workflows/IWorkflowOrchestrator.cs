namespace ICOGenerator.Services.Workflows;

public interface IWorkflowOrchestrator
{
    Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string requirementVersionName, string aiDesignSpecContent);

    /// <param name="coalesceWithActiveRun">
    /// true ⇒ project đã có run "Write Requirement" đang xếp hàng/đang chạy thì TRẢ VỀ chính run đó thay vì
    /// tạo run mới. Chỉ đúng cho đường người dùng bấm nút: lượt bấm đó không mang thêm thông tin nào nên run
    /// thứ hai chỉ sinh lại đúng bản draft ấy. Các đường VỪA GHI một lượt user mới vào hội thoại
    /// (ReviseBriefFromNotesUseCase, RoutePocFeedbackToRequirementUseCase) phải để false — run đang bay đã
    /// đọc transcript từ trước nên không thấy lượt vừa thêm, gộp vào nó là nuốt mất phản hồi của họ.
    /// </param>
    Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false);

    // Khởi tạo workflow nền sinh AI Design Spec từ Product Brief đã duyệt của <paramref name="requirementVersionName"/>.
    // Tách khỏi Approve (vốn chạy đồng bộ, làm treo màn hình) để tiến độ report live; worker tự khởi động
    // delivery workflow dựng POC khi spec sinh xong.
    Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string requirementVersionName);
}
