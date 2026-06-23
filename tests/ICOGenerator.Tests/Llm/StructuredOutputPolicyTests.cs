using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

public class StructuredOutputPolicyTests
{
    private static AiModel Model(string modelId) => new() { ModelId = modelId };

    [Fact]
    public void UsesStructuredOutput_WhenEnabledAndListed_CaseInsensitive()
    {
        var policy = new StructuredOutputPolicy(enabled: true, modelIds: new[] { "GPT-4o-Mini" });

        Assert.True(policy.UseStructuredOutput(Model("gpt-4o-mini")));
        Assert.False(policy.UseStructuredOutput(Model("another-model")));
    }

    [Fact]
    public void DoesNotUseStructuredOutput_WhenEnabledButListEmpty()
    {
        var policy = new StructuredOutputPolicy(enabled: true, modelIds: System.Array.Empty<string>());

        Assert.False(policy.UseStructuredOutput(Model("gpt-4o-mini")));
    }

    [Fact]
    public void DoesNotUseStructuredOutput_WhenDisabled_EvenIfListed()
    {
        // Opt-in default: a listed model still stays on the legacy text path until the feature is enabled.
        var policy = new StructuredOutputPolicy(enabled: false, modelIds: new[] { "gpt-4o-mini" });

        Assert.False(policy.UseStructuredOutput(Model("gpt-4o-mini")));
    }
}
