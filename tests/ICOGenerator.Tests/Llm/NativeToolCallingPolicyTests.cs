using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

public class NativeToolCallingPolicyTests
{
    private static AiModel Model(string modelId) => new() { ModelId = modelId };

    [Fact]
    public void UsesNativeTools_ByDefault_WhenEnabledAndNotListed()
    {
        var policy = new NativeToolCallingPolicy(enabled: true, fallbackModelIds: Array.Empty<string>());

        Assert.True(policy.UseNativeTools(Model("gpt-4o-mini")));
    }

    [Fact]
    public void FallsBack_WhenModelIdListed_CaseInsensitive()
    {
        var policy = new NativeToolCallingPolicy(enabled: true, fallbackModelIds: new[] { "Weak-Local-Model" });

        Assert.False(policy.UseNativeTools(Model("weak-local-model")));
        Assert.True(policy.UseNativeTools(Model("another-model")));
    }

    [Fact]
    public void FallsBack_ForEveryModel_WhenGloballyDisabled()
    {
        var policy = new NativeToolCallingPolicy(enabled: false, fallbackModelIds: new[] { "irrelevant" });

        Assert.False(policy.UseNativeTools(Model("gpt-4o-mini")));
    }
}
