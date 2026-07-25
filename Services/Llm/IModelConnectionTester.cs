using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Bắn một lời gọi chat cực nhỏ tới cấu hình của một <see cref="AiModel"/> để xem endpoint/ApiKey/ModelId
/// có dùng được không. Dùng cho nút "Test Connection" ở trang Models: KHÔNG ghi call log, KHÔNG tính vào
/// budget và không cần project/agent — nó chỉ kiểm tra kết nối trước khi lưu model.
/// </summary>
public interface IModelConnectionTester
{
    Task<ModelConnectionTestOutcome> TestAsync(AiModel model, CancellationToken cancellationToken = default);
}

/// <summary>
/// Kết quả thô của một lần thử kết nối. <paramref name="ErrorMessage"/> là câu ngắn để hiện lên UI,
/// <paramref name="Detail"/> là phần chi tiết (body lỗi của API / nội dung model trả về) để soi thêm.
/// </summary>
public sealed record ModelConnectionTestOutcome(
    bool IsSuccess,
    long DurationMs,
    int? HttpStatusCode = null,
    string? Detail = null,
    string? ErrorMessage = null);
