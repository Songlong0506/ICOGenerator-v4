using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Trần token của MỘT lời gọi. Điểm phải khóa lại: trần bám VÁCH GIÁ (272K token — vượt là cả request bị
// tính 2x input), KHÔNG bám phần trăm context window. Neo vào phần trăm context là sai chiều: model càng
// lớn context thì trần càng cao, trong khi giá thì ngược lại.
public class PromptBudgetTests
{
    private static AiModel Model(int contextWindow) => new() { ModelId = "m", ContextWindow = contextWindow };

    [Fact]
    public void HugeContextModel_IsCappedByThePriceCliff_NotByItsContextWindow()
    {
        var luna = Model(1_050_000);

        // 40% của context window sẽ là 420.000 — nằm sâu trong vùng giá đôi. Trần thật phải thấp hơn vách.
        Assert.True(PromptBudget.Resolve(luna) < PromptBudget.LongContextPriceCliffTokens);

        // 272.000 - 32.000 = 240.000 token THẬT. Không còn hệ số bù nào ở đây: TokenEstimator đếm bằng
        // tokenizer thật nên trần so thẳng với vách giá — xem PromptBudget.
        Assert.Equal(240_000, PromptBudget.Resolve(luna));
        Assert.Equal(80_000, PromptBudget.ConversationTokens(luna));
        Assert.Equal(80_000, PromptBudget.SourceTokens(luna));
        Assert.Equal(40_000, PromptBudget.ImageTokens(luna));
    }

    // Ba quỹ con cộng lại phải còn chừa chỗ cho prompt NỀN cố định, và tổng vẫn phải nằm dưới vách giá —
    // đây là bất biến mà hệ số 5/8 cũ giữ một cách tình cờ (bằng cách siết mọi thứ xuống 62%), nên khi gỡ
    // hệ số đi thì phải khóa nó lại tường minh.
    [Fact]
    public void TheThreeSubBudgets_LeaveRoomForTheBasePromptAndStayUnderThePriceCliff()
    {
        var luna = Model(1_050_000);

        var subBudgets = PromptBudget.ConversationTokens(luna)
                         + PromptBudget.SourceTokens(luna)
                         + PromptBudget.ImageTokens(luna);

        Assert.True(subBudgets < PromptBudget.Resolve(luna),
            $"ba quỹ con ({subBudgets}) phải chừa chỗ cho prompt nền trong trần {PromptBudget.Resolve(luna)}");
        Assert.True(PromptBudget.Resolve(luna) + PromptBudget.OutputReserveTokens
                    <= PromptBudget.LongContextPriceCliffTokens);
    }

    // Model context nhỏ hơn vách giá thì chính context window là cận trên — vách không còn liên quan.
    [Fact]
    public void SmallContextModel_IsCappedByItsOwnContextWindow()
    {
        Assert.Equal(96_000, PromptBudget.Resolve(Model(128_000)));
    }

    // Không được ra số âm hay số vô nghĩa khi context window nhỏ hơn cả phần chừa cho output.
    [Theory]
    [InlineData(8_000)]
    [InlineData(1_000)]
    [InlineData(0)]
    public void TinyOrUnknownContextWindow_StillYieldsAUsableFloor(int contextWindow)
    {
        var budget = PromptBudget.Resolve(Model(contextWindow));

        Assert.True(budget >= PromptBudget.MinimumPromptTokens);
        Assert.True(budget <= 240_000);
    }
}
