using ICOGenerator.Services.Workflows;

namespace ICOGenerator.Application.Requirements;

public class GenerateRequirementDraftUseCase
{
    private readonly IWorkflowOrchestrator _workflowOrchestrator;

    public GenerateRequirementDraftUseCase(IWorkflowOrchestrator workflowOrchestrator)
    {
        _workflowOrchestrator = workflowOrchestrator;
    }

    // Khởi tạo workflow chạy nền để soạn tài liệu requirement; tiến độ được
    // report live qua IWorkflowProgressReporter để UI poll giống luồng Approve.
    //
    // coalesceWithActiveRun: chỉ đường NGƯỜI DÙNG BẤM NÚT mới bật (lượt bấm không mang thông tin mới nên
    // gộp về run đang bay là đúng). Các đường vừa ghi thêm một lượt user vào hội thoại phải để mặc định
    // false, nếu không phản hồi vừa gửi sẽ bị nuốt vào một run đã đọc transcript từ trước.
    public Task ExecuteAsync(Guid projectId, bool coalesceWithActiveRun = false) =>
        _workflowOrchestrator.StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun);
}
