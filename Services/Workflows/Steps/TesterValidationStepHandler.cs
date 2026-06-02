using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Workflows.Steps;

public sealed class TesterValidationStepHandler : WorkflowStepHandlerBase
{
    public TesterValidationStepHandler(AppDbContext db, AgentRunService agentRunService) : base(db, agentRunService)
    {
    }

    public override bool CanHandle(WorkflowExecutionContext context)
        => context.CurrentTask.Type == AgentTaskType.Testing;

    public override async Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.CurrentTask.AgentId == null)
            return WorkflowStepResult.Fail("No Tester agent is assigned to this workflow task.");

        var output = await AgentRunService.RunAsync(
            context.Project.Id,
            context.CurrentTask.AgentId.Value,
            $"""
Hãy đóng vai Tester và validate implementation theo AI Design Spec, Tech Lead plan và Developer report bên dưới.
Chạy test/build phù hợp nếu có thể.

Kết luận cuối cùng cần bắt đầu bằng một trong hai dòng sau:
- TEST_RESULT: PASS
- TEST_RESULT: FAIL

Nếu FAIL, hãy liệt kê bug cụ thể, bước reproduce, expected/actual và đề xuất fix.

# Validation Input

{context.CurrentTask.Input}
""");

        if (!IsFailingTestReport(output))
            return WorkflowStepResult.Complete(output);

        var developer = await FindActiveAgentAsync(AgentRoleKey.Developer, cancellationToken);
        if (developer == null)
            return WorkflowStepResult.Fail("Testing failed, but no active Developer agent is available for bug fixing.", output);

        var nextInput = BuildInputWithPreviousOutput(context.CurrentTask.Input, "Tester Bug Report", output);
        var nextTask = CreateTask(
            context,
            developer.Id,
            AgentTaskType.BugFix,
            "Fix bugs reported by Tester",
            nextInput);

        return WorkflowStepResult.Continue(output, WorkflowStageKey.BugFix, nextTask);
    }

    private static bool IsFailingTestReport(string output)
    {
        if (output.Contains("TEST_RESULT: FAIL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (output.Contains("TEST_RESULT: PASS", StringComparison.OrdinalIgnoreCase))
            return false;

        return output.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("bug", StringComparison.OrdinalIgnoreCase)
            || output.Contains("error", StringComparison.OrdinalIgnoreCase);
    }
}
