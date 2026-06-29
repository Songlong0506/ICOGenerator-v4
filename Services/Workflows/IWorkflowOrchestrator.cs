namespace ICOGenerator.Services.Workflows;

public interface IWorkflowOrchestrator
{
    Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string requirementVersionName, string aiDesignSpecContent);

    Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId);

    // Khởi tạo workflow nền sinh AI Design Spec từ Product Brief đã duyệt của <paramref name="requirementVersionName"/>.
    // Tách khỏi Approve (vốn chạy đồng bộ, làm treo màn hình) để tiến độ report live; worker tự khởi động
    // delivery workflow dựng POC khi spec sinh xong.
    Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string requirementVersionName);
}
