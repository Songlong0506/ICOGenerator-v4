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

    [Fact]
    public void TestingStep_SupportsReworkToDeveloperBugFix()
    {
        var testing = DeliveryPipeline.Find(WorkflowStageKey.Testing);

        Assert.NotNull(testing);
        Assert.NotNull(testing!.Rework);
        Assert.Equal(AgentRoleKey.Developer, testing.Rework!.Role);
        Assert.Equal(AgentTaskType.BugFix, testing.Rework.TaskType);
        Assert.True(testing.Rework.MaxSteps > 0);
    }

    [Fact]
    public void OnlyFinalStep_IsReworkable()
    {
        // Vòng lặp chất lượng chỉ gắn ở bước cuối (Testing); các bước khác không có rework.
        var reworkable = DeliveryPipeline.Steps.Where(s => s.Rework != null).Select(s => s.Stage).ToArray();

        Assert.Equal(new[] { WorkflowStageKey.Testing }, reworkable);
    }

    [Fact]
    public void Find_UnknownStage_ReturnsNull()
    {
        Assert.Null(DeliveryPipeline.Find(WorkflowStageKey.Completed));
    }
}
