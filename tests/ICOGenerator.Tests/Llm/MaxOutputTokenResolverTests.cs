using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Trần output theo model (màn hình AI Models) là van cho endpoint đứng sau gateway cắt lời gọi chạy lâu: một lượt
// agent 50k+ token dài hơn ngưỡng của gateway và chết trước khi có header. Trần phải thắng phép tính từ context.
public class MaxOutputTokenResolverTests
{
    [Fact]
    public void WithoutACeiling_TheRemainingContextIsUsed_UpTo100k()
    {
        Assert.Equal(100000, MaxOutputTokenResolver.Resolve(new AiModel { ContextWindow = 1000000 }, 20000));
        Assert.Equal(64000 - 20000 - 1024, MaxOutputTokenResolver.Resolve(new AiModel { ContextWindow = 64000 }, 20000));
    }

    [Fact]
    public void TheModelCeiling_Wins()
    {
        var model = new AiModel { ContextWindow = 200000, MaxOutputTokens = 16000 };

        Assert.Equal(16000, MaxOutputTokenResolver.Resolve(model, 20000));
    }

    [Fact]
    public void TheContextWindow_StillWins_WhenItIsTighterThanTheCeiling()
    {
        var model = new AiModel { ContextWindow = 32000, MaxOutputTokens = 16000 };

        Assert.Equal(32000 - 25000 - 1024, MaxOutputTokenResolver.Resolve(model, 25000));
    }

    [Fact]
    public void ACeilingBelowTheFloor_IsHonoured()
    {
        Assert.Equal(100, MaxOutputTokenResolver.Resolve(new AiModel { ContextWindow = 0, MaxOutputTokens = 100 }, 0));
        Assert.Equal(100, MaxOutputTokenResolver.Resolve(new AiModel { ContextWindow = 1000, MaxOutputTokens = 100 }, 5000));
    }
}
