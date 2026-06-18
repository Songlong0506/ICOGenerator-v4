using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class DeliveryPipelineTests
{
    [Fact]
    public void First_IsArchitectureDesign_ByTechLead()
    {
        Assert.Equal(WorkflowStageKey.ArchitectureDesign, DeliveryPipeline.First.Stage);
        Assert.Equal(AgentRoleKey.TechLead, DeliveryPipeline.First.Role);
        Assert.Equal(AgentTaskType.ArchitectureDesign, DeliveryPipeline.First.TaskType);
        Assert.False(DeliveryPipeline.First.RequiresApproval);
    }

    [Fact]
    public void Next_AfterArchitecture_IsGatedImplementationByDeveloper()
    {
        var next = DeliveryPipeline.Next(WorkflowStageKey.ArchitectureDesign);

        Assert.NotNull(next);
        Assert.Equal(WorkflowStageKey.Implementation, next!.Stage);
        Assert.Equal(AgentRoleKey.Developer, next.Role);
        // The human-approval gate sits in front of the developer's coding step.
        Assert.True(next.RequiresApproval);
    }

    [Fact]
    public void Next_AfterImplementation_IsTestingByTester_NoGate()
    {
        var next = DeliveryPipeline.Next(WorkflowStageKey.Implementation);

        Assert.NotNull(next);
        Assert.Equal(WorkflowStageKey.Testing, next!.Stage);
        Assert.Equal(AgentRoleKey.Tester, next.Role);
        Assert.False(next.RequiresApproval);
    }

    [Fact]
    public void Next_AfterLastStep_IsNull()
    {
        Assert.Null(DeliveryPipeline.Next(WorkflowStageKey.Testing));
    }

    [Theory]
    [InlineData(WorkflowStageKey.RequirementApproved)]
    [InlineData(WorkflowStageKey.Completed)]
    [InlineData(WorkflowStageKey.Failed)]
    public void Next_ForStageNotInPipeline_IsNull(WorkflowStageKey stage)
    {
        Assert.Null(DeliveryPipeline.Next(stage));
    }

    [Fact]
    public void Steps_HaveUniqueStages_InHandOffOrder()
    {
        var stages = DeliveryPipeline.Steps.Select(s => s.Stage).ToList();
        Assert.Equal(stages.Distinct().Count(), stages.Count);
    }
}
