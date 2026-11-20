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

    // Ghi chú hoãn đi vào vòng dựng mới SAU bộ quy ước đã chốt: quy ước là thứ đã được kiểm qua một vòng
    // sửa, ghi chú hoãn là góp ý mới nhất — đọc theo thứ tự cũ → mới.
    [Fact]
    public void Build_WithDeferredFeedbackBlock_AppendsItAfterTheConventionsBlock()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false,
            conventionsBlock: "CONVENTIONS-BLOCK", deferredFeedbackBlock: "DEFERRED-BLOCK");

        Assert.True(prompt.IndexOf("CONVENTIONS-BLOCK", StringComparison.Ordinal) < prompt.IndexOf("DEFERRED-BLOCK", StringComparison.Ordinal));
        Assert.Equal(
            builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false),
            builder.Build(AgentTaskType.PocPreview, "the spec", useBoschTemplate: false, deferredFeedbackBlock: ""));
    }

    // Vòng tự sửa của cổng kịch bản nghiệm thu: prompt POC gốc (để agent giữ đúng luật dựng bản demo) + bộ UAT
    // + khối poc-uat-fix — KHÔNG phải khối revision, vì đây là máy báo chứ không phải người duyệt nhận xét.
    [Fact]
    public void BuildPocUatFix_AppendsTheGateBlock_NotTheRevisionBlock()
    {
        var builder = new WorkflowTaskPromptBuilder(new StubPrompts());

        var prompt = builder.BuildPocUatFix("the spec", "UAT-BLOCK", "CONVENTIONS-BLOCK", "poc v1", "1. Kịch bản A gãy");

        Assert.StartsWith("BASE:the spec", prompt);
        Assert.Contains("UAT-BLOCK", prompt);
        Assert.Contains("CONVENTIONS-BLOCK", prompt);
        Assert.Contains("UATFIX|prev=poc v1|failures=1. Kịch bản A gãy", prompt);
        Assert.DoesNotContain("REVISION", prompt);
        Assert.True(prompt.IndexOf("UAT-BLOCK", StringComparison.Ordinal) < prompt.IndexOf("UATFIX", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPocUatFix_WithoutPreviousOutput_PointsAtTheWorkspace()
    {
        var prompt = new WorkflowTaskPromptBuilder(new StubPrompts())
            .BuildPocUatFix("the spec", null, null, previousOutput: null, failures: "1. x");

        Assert.DoesNotContain("{{previous_output}}", prompt);
        Assert.Contains("workspace", prompt);
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => relativePath switch
        {
            "Shared/revision.v1.md" => "REVISION|prev={{previous_output}}|fb={{feedback}}",
            "Developer/poc-uat-fix.v1.md" => "UATFIX|prev={{previous_output}}|failures={{failures}}",
            _ => "BASE:{{input}}"
        };
    }
}
