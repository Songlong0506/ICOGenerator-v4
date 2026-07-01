namespace ICOGenerator.Services.Llm;

/// <summary>
/// Nguồn sự thật DUY NHẤT để quy token ra USD theo đơn giá model. Dùng chung bởi báo cáo Usage
/// (<c>GetUsageOverviewQuery</c>) và circuit-breaker ngân sách (<c>BudgetGuard</c>) để cả hai đo chi phí
/// GIỐNG HỆT nhau — admin đặt trần dựa trên số ở trang Usage thì guard phải tính ra đúng con số đó.
/// </summary>
public static class LlmCost
{
    public static decimal Usd(long promptTokens, long completionTokens, decimal inputPricePerMillionTokens, decimal outputPricePerMillionTokens)
        => promptTokens / 1_000_000m * inputPricePerMillionTokens
         + completionTokens / 1_000_000m * outputPricePerMillionTokens;
}
