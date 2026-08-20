using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// LlmCost là nguồn sự thật DUY NHẤT quy token ra USD (trang Usage, bảng Quality, Prompt Evals, BudgetGuard).
// Sai ở đây là sai đồng loạt ở cả bốn chỗ, và sai theo hướng không ai nhận ra — nên khóa công thức lại.
public class LlmCostTests
{
    private static readonly LlmPrice Price = new(Input: 2m, CachedInput: 0.2m, Output: 10m);

    [Fact]
    public void Usd_WithoutCache_ChargesEveryPromptTokenAtFullInputPrice()
    {
        // 1M input @ $2 + 1M output @ $10
        Assert.Equal(12m, LlmCost.Usd(1_000_000, 0, 1_000_000, Price));
    }

    [Fact]
    public void Usd_CachedTokens_AreBilledAtTheCachedRate_AndNotTwice()
    {
        // 1M prompt, trong đó 800K đọc từ cache: 200K @ $2 + 800K @ $0.2 = 0.4 + 0.16
        Assert.Equal(0.56m, LlmCost.Usd(1_000_000, 800_000, 0, Price));
    }

    // Giá cache = 0 nghĩa là CHƯA KHAI BÁO, không phải "miễn phí": mọi model có sẵn trước khi có cột này
    // đều để 0, và tính token cache thành miễn phí sẽ làm mọi báo cáo tụt xuống một con số không ai đặt ra.
    [Fact]
    public void Usd_UndeclaredCachedPrice_FallsBackToFullInputPrice()
    {
        var noCachedPrice = new LlmPrice(Input: 2m, CachedInput: 0m, Output: 10m);

        Assert.Equal(
            LlmCost.Usd(1_000_000, 0, 0, noCachedPrice),
            LlmCost.Usd(1_000_000, 800_000, 0, noCachedPrice));
    }

    // Trang Usage vẽ biểu đồ tháng bằng cách cộng riêng vế prompt và vế completion rồi coi tổng là chi phí
    // của tháng. Tính chất này phải đúng KỂ CẢ khi phần cache có đơn giá riêng, nếu không cột $ và số tổng
    // trong cùng một card sẽ lệch nhau.
    [Fact]
    public void Usd_IsSeparable_BetweenPromptAndCompletion()
    {
        var whole = LlmCost.Usd(900_000, 500_000, 300_000, Price);
        var split = LlmCost.Usd(900_000, 500_000, 0, Price) + LlmCost.Usd(0, 0, 300_000, Price);

        Assert.Equal(whole, split);
    }

    // Cộng dồn theo dòng phải bằng tính trên nhóm đã gộp — Usage/BudgetGuard đều gộp ở DB rồi mới quy USD.
    [Fact]
    public void Usd_IsAdditive_AcrossRows()
    {
        var summed = LlmCost.Usd(1000, 400, 50, Price) + LlmCost.Usd(3000, 100, 70, Price);

        Assert.Equal(LlmCost.Usd(4000, 500, 120, Price), summed);
    }

    // Một endpoint trả cached > prompt không được phép đẩy phần phải trả tiền xuống âm: cộng dồn xong nó sẽ
    // ăn bớt chi phí của những dòng khác và làm trần ngân sách không bao giờ chạm tới.
    [Theory]
    [InlineData(1_000_000L, 5_000_000L)]
    [InlineData(1_000_000L, -7L)]
    public void Usd_OutOfRangeCachedCount_StaysWithinPromptBounds(long prompt, long cached)
    {
        var cost = LlmCost.Usd(prompt, cached, 0, Price);

        var allCached = LlmCost.Usd(prompt, prompt, 0, Price);
        var noneCached = LlmCost.Usd(prompt, 0, 0, Price);
        Assert.InRange(cost, allCached, noneCached);
    }
}
