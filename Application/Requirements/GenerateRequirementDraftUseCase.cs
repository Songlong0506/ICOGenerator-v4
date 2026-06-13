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
    public Task ExecuteAsync(Guid projectId) =>
        _workflowOrchestrator.StartRequirementDraftWorkflowAsync(projectId);
}
