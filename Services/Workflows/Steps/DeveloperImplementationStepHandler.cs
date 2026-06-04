using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Workflows.Steps;

public sealed class DeveloperImplementationStepHandler : WorkflowStepHandlerBase
{
    public DeveloperImplementationStepHandler(AppDbContext db, AgentRunService agentRunService) : base(db, agentRunService)
    {
    }

    public override bool CanHandle(WorkflowExecutionContext context)
        => context.CurrentTask.Type == AgentTaskType.Implementation;

    public override async Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.CurrentTask.AgentId == null)
            return WorkflowStepResult.Fail("No Developer agent is assigned to this workflow task.");

        var output = await AgentRunService.RunAsync(
            context.Project.Id,
            context.CurrentTask.AgentId.Value,
            $"""
User đã approve requirement.

Hãy đóng vai Developer và implement sản phẩm theo AI Design Spec và Tech Lead plan bên dưới.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.
Sau khi code xong, hãy chạy build/test phù hợp và báo cáo kết quả.

# Delivery Input

{context.CurrentTask.Input}
""");

        var tester = await FindActiveAgentAsync(AgentRoleKey.Tester, cancellationToken);
        if (tester == null)
            return WorkflowStepResult.Complete(output);

        var nextInput = BuildInputWithPreviousOutput(context.CurrentTask.Input, "Developer Implementation Report", output);
        var nextTask = CreateTask(
            context,
            tester.Id,
            AgentTaskType.Testing,
            "Validate implementation against approved AI Design Spec",
            nextInput);

        return WorkflowStepResult.Continue(output, WorkflowStageKey.Testing, nextTask);
    }
}
