using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Bảng đơn giá model đã nạp sẵn, tra theo <c>ModelId</c>. Đi kèm <see cref="LlmCost"/>: <c>LlmCost</c> là
/// công thức quy token ra USD, còn class này là NGUỒN DUY NHẤT của bước "tra đơn giá của một ModelId".
/// <para>
/// Vì sao tra bằng chuỗi <c>ModelId</c> chứ không bằng khóa ngoại: <c>AgentModelCallLog</c> chỉ lưu ModelId
/// dạng chuỗi (log phải sống sót khi bản ghi <c>AiModel</c> bị xóa/đổi). Cùng một ModelId có thể có nhiều
/// bản ghi <c>AiModel</c> (khác endpoint) ⇒ gộp lại, lấy bản KHAI TRƯỚC (<c>CreatedAt</c> sớm nhất, đồng
/// hạng thì theo <c>Id</c>) — xem lý do phải sắp xếp ở <see cref="LoadAsync"/>.
/// </para>
/// <para>
/// Ba chỗ đọc bảng này — trang Usage, bảng chất lượng và circuit-breaker ngân sách — TỪNG mỗi nơi chép một
/// bản truy vấn + hàm <c>CostFor</c> riêng. Đó là cách con số ở màn hình Usage và trần <c>BudgetGuard</c>
/// trôi lệch nhau mà không ai thấy: admin đặt trần theo số họ đọc được, guard lại tính bằng bản sao khác.
/// Gộp về một chỗ để cả ba luôn đo giống hệt nhau.
/// </para>
/// </summary>
public sealed class ModelPriceBook
{
    private readonly IReadOnlyDictionary<string, LlmPrice> _byModelId;

    private ModelPriceBook(IReadOnlyDictionary<string, LlmPrice> byModelId) => _byModelId = byModelId;

    /// <summary>Nạp toàn bộ đơn giá đang cấu hình (bảng <c>AiModels</c> nhỏ nên đọc trọn một lượt).</summary>
    public static async Task<ModelPriceBook> LoadAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // ORDER BY là BẮT BUỘC, không phải trang trí. Bên dưới lấy g.First() cho mỗi ModelId trùng, mà
        // truy vấn không sắp xếp thì thứ tự dòng do DB quyết định — hai bản ghi cùng ModelId khác giá sẽ
        // cho ra đơn giá khác nhau giữa các lần chạy (chính chỗ này từng làm ModelPriceBookTests đỏ ~50%
        // số lần: EF chèn theo khóa chính Guid ngẫu nhiên nên "bản đầu" là bản nào tùy hên xui). Với ba
        // nơi đọc bảng này — Usage, bảng chất lượng, BudgetGuard — một đơn giá không tất định nghĩa là
        // ba con số có thể lệch nhau trong cùng một khoảnh khắc, đúng thứ class này sinh ra để chặn.
        // Lấy bản KHAI TRƯỚC: bản gõ giá đầu tiên là bản admin đang nhìn thấy lâu nhất; Id chỉ là chốt
        // phá hòa cho hai bản ghi trùng cả CreatedAt.
        var rows = await db.AiModels
            .AsNoTracking()
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new { m.ModelId, m.InputPricePerMillionTokens, m.CachedInputPricePerMillionTokens, m.OutputPricePerMillionTokens })
            .ToListAsync(cancellationToken);

        var byModelId = rows
            .GroupBy(m => m.ModelId)
            .ToDictionary(
                g => g.Key ?? string.Empty,
                g => new LlmPrice(g.First().InputPricePerMillionTokens, g.First().CachedInputPricePerMillionTokens, g.First().OutputPricePerMillionTokens),
                StringComparer.OrdinalIgnoreCase);

        return new ModelPriceBook(byModelId);
    }

    /// <summary>Có ÍT NHẤT một model được khai giá — cả trang Usage lẫn bảng chất lượng dùng cờ này để
    /// quyết định có hiện cột chi phí hay không (chưa khai giá nào thì mọi số $ đều là 0 gây hiểu nhầm).</summary>
    public bool HasAnyPricing => _byModelId.Values.Any(p => p.Input > 0 || p.Output > 0);

    /// <summary>Đơn giá của một model; model không còn trong bảng ⇒ <c>default</c> (mọi vế bằng 0).</summary>
    public LlmPrice PriceFor(string? modelId)
        => modelId != null && _byModelId.TryGetValue(modelId, out var price) ? price : default;

    /// <summary>Model này có khai giá thật hay không (0 hết ⇒ tự host / đã xóa ⇒ không tính tiền).</summary>
    public bool HasPrice(string? modelId)
    {
        var price = PriceFor(modelId);
        return price.Input > 0 || price.Output > 0;
    }

    /// <summary>Quy token của một (nhóm) lời gọi ra USD. Model không có giá ⇒ 0.</summary>
    public decimal CostFor(string? modelId, long promptTokens, long cachedPromptTokens, long completionTokens)
        => modelId != null && _byModelId.TryGetValue(modelId, out var price)
            ? LlmCost.Usd(promptTokens, cachedPromptTokens, completionTokens, price)
            : 0m;
}
