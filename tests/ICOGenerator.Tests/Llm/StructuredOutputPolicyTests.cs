using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

public class StructuredOutputPolicyTests
{
    private static AiModel Model(bool supportsStructuredOutput) =>
        new() { ModelId = "gpt-4o-mini", SupportsStructuredOutput = supportsStructuredOutput };

    [Fact]
    public void UsesStructuredOutput_WhenModelOptedIn()
    {
        var policy = new StructuredOutputPolicy();

        Assert.True(policy.UseStructuredOutput(Model(supportsStructuredOutput: true)));
    }

    [Fact]
    public void DoesNotUseStructuredOutput_WhenModelNotOptedIn()
    {
        // Opt-in default: a model stays on the legacy text path until SupportsStructuredOutput is ticked.
        var policy = new StructuredOutputPolicy();

        Assert.False(policy.UseStructuredOutput(Model(supportsStructuredOutput: false)));
    }
}
