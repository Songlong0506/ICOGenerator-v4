using System.Collections.Concurrent;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Dòng tiến độ MỚI NHẤT của các biến thể POC đang chạy, giữ trong bộ nhớ (singleton). Một lượt dựng POC
/// kéo dài nhiều phút; bảng so sánh chỉ có "Đang chạy" thì người thử không biết nó đang dựng màn hình
/// thứ mấy hay đã treo. Không ghi DB: đây là thông tin tạm, mất khi restart cũng không sao (worker đánh
/// Failed các lượt dở dang lúc khởi động), còn ghi DB mỗi milestone là một UPDATE cho mỗi tool call.
/// </summary>
public sealed class PocVariantProgress
{
    private readonly ConcurrentDictionary<Guid, string> _latest = new();

    public void Report(Guid variantId, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _latest[variantId] = message.Length > 300 ? message[..300] + "…" : message;
    }

    public string? Get(Guid variantId) => _latest.TryGetValue(variantId, out var message) ? message : null;

    public void Clear(Guid variantId) => _latest.TryRemove(variantId, out _);
}
