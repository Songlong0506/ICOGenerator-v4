using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Workflows.Steps;

public sealed class TechLeadDesignStepHandler : WorkflowStepHandlerBase
{
    public TechLeadDesignStepHandler(AppDbContext db, AgentRunService agentRunService) : base(db, agentRunService)
    {
    }

    public override bool CanHandle(WorkflowExecutionContext context)
        => context.CurrentTask.Type == AgentTaskType.ArchitectureDesign;

    public override async Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.CurrentTask.AgentId == null)
            return WorkflowStepResult.Fail("No Tech Lead agent is assigned to this workflow task.");

        var output = await AgentRunService.RunAsync(
            context.Project.Id,
            context.CurrentTask.AgentId.Value,
            $"""
User đã approve requirement.

Hãy đóng vai Tech Lead và tạo implementation plan cho Developer.
Chỉ sử dụng AI Design Spec bên dưới làm nguồn sự thật.
Không sửa requirement document.

Output cần có:
- Kiến trúc đề xuất
- Các module cần tạo/sửa
- Thứ tự implementation
- Technical risks
- Checklist để Developer build/test

# AI Design Spec

{context.CurrentTask.Input}
""");

        var developer = await FindActiveAgentAsync(AgentRoleKey.Developer, cancellationToken);
        if (developer == null)
            return WorkflowStepResult.Fail("No active Developer agent is available.", output);

        var nextInput = BuildInputWithPreviousOutput(context.CurrentTask.Input, "Tech Lead Implementation Plan", output);
        var nextTask = CreateTask(
            context,
            developer.Id,
            AgentTaskType.Implementation,
            "Implement approved AI Design Spec from Tech Lead plan",
            nextInput);

        return WorkflowStepResult.Continue(output, WorkflowStageKey.Implementation, nextTask);
    }
}
