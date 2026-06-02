using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Workflows.Steps;

public sealed class DeveloperBugFixStepHandler : WorkflowStepHandlerBase
{
    public DeveloperBugFixStepHandler(AppDbContext db, AgentRunService agentRunService) : base(db, agentRunService)
    {
    }

    public override bool CanHandle(WorkflowExecutionContext context)
        => context.CurrentTask.Type == AgentTaskType.BugFix;

    public override async Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.CurrentTask.AgentId == null)
            return WorkflowStepResult.Fail("No Developer agent is assigned to this bug fix task.");

        var output = await AgentRunService.RunAsync(
            context.Project.Id,
            context.CurrentTask.AgentId.Value,
            $"""
Hãy đóng vai Developer và fix bug theo Tester report bên dưới.
Không sửa requirement document.
Sau khi fix xong, chạy build/test phù hợp và báo cáo kết quả.

# Bug Fix Input

{context.CurrentTask.Input}
""");

        var tester = await FindActiveAgentAsync(AgentRoleKey.Tester, cancellationToken);
        if (tester == null)
            return WorkflowStepResult.Complete(output);

        var nextInput = BuildInputWithPreviousOutput(context.CurrentTask.Input, "Developer Bug Fix Report", output);
        var nextTask = CreateTask(
            context,
            tester.Id,
            AgentTaskType.Testing,
            "Retest implementation after bug fix",
            nextInput);

        return WorkflowStepResult.Continue(output, WorkflowStageKey.Testing, nextTask);
    }
}
