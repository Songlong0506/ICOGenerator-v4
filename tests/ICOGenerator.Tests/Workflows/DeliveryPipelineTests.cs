using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class DeliveryPipelineTests
{
    [Fact]
    public void Find_ReturnsBugFixStep_ForBugFixStage()
    {
        var step = DeliveryPipeline.Find(WorkflowStageKey.BugFix);

        Assert.NotNull(step);
        Assert.Equal(AgentRoleKey.Developer, step!.Role);
        Assert.Equal(AgentTaskType.BugFix, step.TaskType);
    }

    [Fact]
    public void Next_DoesNotIncludeBugFix_KeepingPipelineLinear()
    {
        // BugFix là CHU TRÌNH quanh Testing, không phải hand-off tuyến tính: Testing là bước cuối
        // của chuỗi tuyến tính (Next = null), và BugFix không nằm trong chuỗi đó.
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.Testing));
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.BugFix));
    }

    [Fact]
    public void TestingStep_ResolvesToTesterStage()
    {
        Assert.Equal(WorkflowStageKey.Testing, DeliveryPipeline.TestingStep.Stage);
        Assert.Equal(AgentRoleKey.Tester, DeliveryPipeline.TestingStep.Role);
    }

    [Fact]
    public void Next_AfterImplementation_IsCodeReviewByTechLead()
    {
        var step = DeliveryPipeline.Next(WorkflowStageKey.Implementation);

        Assert.NotNull(step);
        Assert.Equal(WorkflowStageKey.CodeReview, step!.Stage);
        Assert.Equal(AgentRoleKey.TechLead, step.Role);
        Assert.Equal(AgentTaskType.CodeReview, step.TaskType);
    }

    [Fact]
    public void Next_AfterCodeReview_IsTesting()
    {
        // Code review chèn TRƯỚC Testing: review xong (qua cổng duyệt) thì sang Tester.
        var step = DeliveryPipeline.Next(WorkflowStageKey.CodeReview);

        Assert.NotNull(step);
        Assert.Equal(WorkflowStageKey.Testing, step!.Stage);
    }

    [Fact]
    public void Find_ReturnsCodeReviewStep_ForCodeReviewStage()
    {
        var step = DeliveryPipeline.Find(WorkflowStageKey.CodeReview);

        Assert.NotNull(step);
        Assert.Equal(AgentRoleKey.TechLead, step!.Role);
        Assert.Equal(AgentTaskType.CodeReview, step.TaskType);
    }

    [Fact]
    public void MaxBugFixAttempts_IsPositive()
        => Assert.True(DeliveryPipeline.MaxBugFixAttempts > 0);
}
