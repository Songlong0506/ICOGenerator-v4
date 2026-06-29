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
        // BugFix là CHU TRÌNH quanh Testing, không phải hand-off tuyến tính: nó không nằm trong chuỗi
        // tuyến tính nên Next(BugFix) = null. (Sau Testing là bước Tạo Pull Request — xem test riêng.)
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.BugFix));
    }

    [Fact]
    public void Next_AfterTesting_IsPullRequestByDeveloper()
    {
        // Đóng vòng giao hàng: test PASS (qua cổng duyệt) thì sang bước Tạo Pull Request.
        var step = DeliveryPipeline.Next(WorkflowStageKey.Testing);

        Assert.NotNull(step);
        Assert.Equal(WorkflowStageKey.PullRequest, step!.Stage);
        Assert.Equal(AgentRoleKey.Developer, step.Role);
        Assert.Equal(AgentTaskType.PullRequest, step.TaskType);
    }

    [Fact]
    public void Next_AfterPullRequest_IsNull_BecauseItIsTheLastStep()
        => Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.PullRequest));

    [Fact]
    public void TestingStep_ResolvesToTesterStage()
    {
        Assert.Equal(WorkflowStageKey.Testing, DeliveryPipeline.TestingStep.Stage);
        Assert.Equal(AgentRoleKey.Tester, DeliveryPipeline.TestingStep.Role);
    }

    [Fact]
    public void Next_AfterPocPreview_IsTechnicalDocsByBusinessAnalyst()
    {
        // Bước 2 của pipeline: duyệt POC xong thì sinh tài liệu kỹ thuật (BRD/SRS/FSD/UserStories).
        var step = DeliveryPipeline.Next(WorkflowStageKey.PocPreview);

        Assert.NotNull(step);
        Assert.Equal(WorkflowStageKey.TechnicalDocs, step!.Stage);
        Assert.Equal(AgentRoleKey.BusinessAnalyst, step.Role);
        Assert.Equal(AgentTaskType.TechnicalDocs, step.TaskType);
    }

    [Fact]
    public void Next_AfterTechnicalDocs_IsArchitectureDesignByTechLead()
    {
        // Sau tài liệu kỹ thuật mới tới kiến trúc — phần đắt (code) vẫn nằm sau các cổng duyệt rẻ.
        var step = DeliveryPipeline.Next(WorkflowStageKey.TechnicalDocs);

        Assert.NotNull(step);
        Assert.Equal(WorkflowStageKey.ArchitectureDesign, step!.Stage);
        Assert.Equal(AgentRoleKey.TechLead, step.Role);
        Assert.Equal(AgentTaskType.ArchitectureDesign, step.TaskType);
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
