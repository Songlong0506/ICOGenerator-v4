using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class DeliveryPipelineTests
{
    [Fact]
    public void First_IsPocPreview()
    {
        Assert.Equal(WorkflowStageKey.PocPreview, DeliveryPipeline.First.Stage);
    }

    [Fact]
    public void Steps_FollowQualityLoopOrder()
    {
        var stages = DeliveryPipeline.Steps.Select(s => s.Stage).ToArray();

        Assert.Equal(new[]
        {
            WorkflowStageKey.PocPreview,
            WorkflowStageKey.UiUxDesign,
            WorkflowStageKey.ArchitectureDesign,
            WorkflowStageKey.Implementation,
            WorkflowStageKey.CodeReview,
            WorkflowStageKey.Testing
        }, stages);
    }

    [Theory]
    [InlineData(WorkflowStageKey.PocPreview, WorkflowStageKey.UiUxDesign)]
    [InlineData(WorkflowStageKey.UiUxDesign, WorkflowStageKey.ArchitectureDesign)]
    [InlineData(WorkflowStageKey.ArchitectureDesign, WorkflowStageKey.Implementation)]
    [InlineData(WorkflowStageKey.Implementation, WorkflowStageKey.CodeReview)]
    [InlineData(WorkflowStageKey.CodeReview, WorkflowStageKey.Testing)]
    public void Next_AdvancesThroughPipeline(WorkflowStageKey current, WorkflowStageKey expected)
    {
        Assert.Equal(expected, DeliveryPipeline.Next(current)?.Stage);
    }

    [Fact]
    public void Next_OfFinalStage_IsNull()
    {
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.Testing));
    }

    [Fact]
    public void Next_OfUnknownStage_IsNull()
    {
        // Stage không thuộc pipeline giao hàng (vd giai đoạn requirement).
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.RequirementApproved));
    }

    [Theory]
    [InlineData(WorkflowStageKey.CodeReview)]
    [InlineData(WorkflowStageKey.Testing)]
    public void QualityGate_SupportsReworkToDeveloperBugFix(WorkflowStageKey stage)
    {
        var step = DeliveryPipeline.Find(stage);

        Assert.NotNull(step);
        Assert.NotNull(step!.Rework);
        Assert.Equal(AgentRoleKey.Developer, step.Rework!.Role);
        Assert.Equal(AgentTaskType.BugFix, step.Rework.TaskType);
        Assert.True(step.Rework.MaxSteps > 0);
    }

    [Fact]
    public void OnlyQualityGates_AreReworkable()
    {
        // Vòng lặp chất lượng gắn ở hai cổng kiểm soát chất lượng (Code Review + Testing);
        // các bước còn lại không có rework.
        var reworkable = DeliveryPipeline.Steps.Where(s => s.Rework != null).Select(s => s.Stage).ToArray();

        Assert.Equal(new[] { WorkflowStageKey.CodeReview, WorkflowStageKey.Testing }, reworkable);
    }

    [Fact]
    public void Find_UnknownStage_ReturnsNull()
    {
        Assert.Null(DeliveryPipeline.Find(WorkflowStageKey.Completed));
    }
}
