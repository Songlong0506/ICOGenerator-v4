using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// The revision block is appended AFTER the step's normal prompt (so the original task contract
// stays intact) and only when reviewer feedback is present; a missing previous hand-off falls
// back to a note pointing the agent at the workspace instead of leaving a dangling placeholder.
public class WorkflowTaskPromptBuilderTests
{
    [Fact]
    public void Build_WithoutFeedback_ReturnsBasePromptOnly()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.ArchitectureDesign, "the spec", useBoschTemplate: false);

        Assert.Equal("BASE:the spec", prompt);
        Assert.DoesNotContain("REVISION", prompt);
    }

    [Fact]
    public void Build_WithFeedback_AppendsRevisionBlockAfterBasePrompt()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.ArchitectureDesign, "the spec", useBoschTemplate: false,
            revisionFeedback: "thiếu ERD", previousOutput: "architecture v1");

        Assert.StartsWith("BASE:the spec", prompt);
        Assert.Contains("REVISION|prev=architecture v1|fb=thiếu ERD", prompt);
    }

    [Fact]
    public void Build_WithFeedbackButNoPreviousOutput_PointsAgentAtWorkspace()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false,
            revisionFeedback: "đổi màu header", previousOutput: null);

        Assert.Contains("fb=đổi màu header", prompt);
        Assert.Contains("không có bàn giao lần trước", prompt);
        Assert.DoesNotContain("{{previous_output}}", prompt);
    }

    [Fact]
    public void Build_WithBlankFeedback_DoesNotAppendRevisionBlock()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.Testing, "handoff", useBoschTemplate: false,
            revisionFeedback: "   ", previousOutput: "old");

        Assert.Equal("BASE:handoff", prompt);
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) =>
            relativePath == "Shared/revision.v1.md"
                ? "REVISION|prev={{previous_output}}|fb={{feedback}}"
                : "BASE:{{input}}";
    }
}
