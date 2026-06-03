using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Workflows.Steps;

public class ImplementationStepHandler : IWorkflowStepHandler
{
    private readonly AgentRunService _agentRunService;

    public ImplementationStepHandler(AgentRunService agentRunService)
    {
        _agentRunService = agentRunService;
    }

    public AgentTaskType TaskType => AgentTaskType.Implementation;

    public async Task<WorkflowStepResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (task.AgentId == null)
            return WorkflowStepResult.Failed("No agent is assigned to this task.");

        var output = await _agentRunService.RunAsync(
            task.ProjectId,
            task.AgentId.Value,
            $"""
User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

# AI Design Spec

{task.Input}
""");

        return WorkflowStepResult.Completed(output);
    }
}
