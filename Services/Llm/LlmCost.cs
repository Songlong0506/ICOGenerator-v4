namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đơn giá của một model, gói lại thành MỘT tham số. Ba số cùng kiểu <c>decimal</c> đứng rời nhau trong
/// danh sách tham số là chỗ đảo nhầm thứ tự mà compiler không bao giờ bắt được, và một lần đảo giữa
/// input/output là mọi con số chi phí trong app sai theo cùng một hướng.
/// </summary>
/// <param name="Input">Giá token input KHÔNG được cache ($/1M).</param>
/// <param name="CachedInput">Giá token input được phục vụ từ cache prompt ($/1M). 0 = CHƯA KHAI BÁO.</param>
/// <param name="Output">Giá token output ($/1M).</param>
public readonly record struct LlmPrice(decimal Input, decimal CachedInput, decimal Output)
{
    /// <summary>
    /// Giá thực áp cho token đã cache. <see cref="CachedInput"/> bằng 0 nghĩa là "chưa khai báo" chứ không
    /// phải "miễn phí": mọi model đã có trong DB trước khi có cột này đều để 0, và tính token cache thành
    /// miễn phí sẽ làm trang Usage lẫn trần ngân sách tụt xuống một con số không ai đặt ra. Rơi về giá
    /// input đầy đủ ⇒ giữ nguyên hành vi cũ cho tới khi admin điền giá cache ở màn hình <b>AI Models</b>.
    /// Không provider nào bán cached input miễn phí, nên "0 = miễn phí" cũng không mất trường hợp thật nào.
    /// </summary>
    public decimal EffectiveCachedInput => CachedInput > 0 ? CachedInput : Input;
}

/// <summary>
/// Nguồn sự thật DUY NHẤT để quy token ra USD theo đơn giá model. Dùng chung bởi báo cáo Usage
/// (<c>GetUsageOverviewQuery</c>), bảng chất lượng (<c>GetDeliveryQualityQuery</c>), Prompt Evals và
/// circuit-breaker ngân sách (<c>BudgetGuard</c>) để tất cả đo chi phí GIỐNG HỆT nhau — admin đặt trần dựa
/// trên số ở trang Usage thì guard phải tính ra đúng con số đó.
/// </summary>
public static class LlmCost
{
    /// <summary>
    /// Quy token của một (nhóm) lời gọi ra USD.
    /// </summary>
    /// <param name="promptTokens">
    /// TỔNG token input, đã BAO GỒM phần được cache (đúng nghĩa <c>usage.prompt_tokens</c> của OpenAI).
    /// </param>
    /// <param name="cachedPromptTokens">
    /// Phần nằm TRONG <paramref name="promptTokens"/> được phục vụ từ cache. Kẹp về [0, promptTokens]:
    /// một endpoint trả số vô lý không được phép đẩy phần phải trả tiền xuống âm và ăn bớt chi phí của
    /// các dòng khác khi cộng dồn.
    /// </param>
    public static decimal Usd(long promptTokens, long cachedPromptTokens, long completionTokens, LlmPrice price)
    {
        var billablePrompt = Math.Max(promptTokens, 0);
        var cached = Math.Clamp(cachedPromptTokens, 0, billablePrompt);

        return (billablePrompt - cached) / 1_000_000m * price.Input
             + cached / 1_000_000m * price.EffectiveCachedInput
             + completionTokens / 1_000_000m * price.Output;
    }
}
