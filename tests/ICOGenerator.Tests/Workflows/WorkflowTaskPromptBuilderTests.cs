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

    // Khối quy ước trình bày là đường DUY NHẤT các góp ý giao diện đã được chấp nhận tới được agent sau
    // khi poc-demo.html bị dựng lại từ template — nó phải nằm SAU cả input lẫn khối nghiệm thu, và phải
    // biến mất hoàn toàn khi dự án chưa có quy ước nào (đừng đổi prompt của dự án không liên quan).
    [Fact]
    public void Build_WithConventionsBlock_AppendsItAfterTheAcceptanceBlock()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false,
            acceptanceBlock: "UAT-BLOCK", conventionsBlock: "CONVENTIONS-BLOCK");

        Assert.StartsWith("BASE:the spec", prompt);
        Assert.True(prompt.IndexOf("UAT-BLOCK", StringComparison.Ordinal) < prompt.IndexOf("CONVENTIONS-BLOCK", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithoutConventionsBlock_LeavesThePromptUnchanged()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        Assert.Equal(
            builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false),
            builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false, conventionsBlock: "   "));
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
