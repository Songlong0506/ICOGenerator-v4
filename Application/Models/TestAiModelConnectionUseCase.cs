using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Models;

/// <summary>
/// Thử kết nối tới cấu hình model đang gõ trong modal Add/Edit của trang Models, để người dùng biết ngay
/// endpoint/ApiKey/ModelId có dùng được không thay vì phải lưu rồi đi chạy một agent thật mới phát hiện sai.
/// Không ghi gì xuống DB: chỉ đọc ApiKey đã lưu khi form Edit để trống trường đó (đúng luật "để trống = giữ
/// key hiện tại" của <see cref="UpdateAiModelUseCase"/>, vì key thật không bao giờ được gửi về browser).
/// </summary>
public class TestAiModelConnectionUseCase
{
    private readonly AppDbContext _db;
    private readonly IModelConnectionTester _tester;

    public TestAiModelConnectionUseCase(AppDbContext db, IModelConnectionTester tester)
    {
        _db = db;
        _tester = tester;
    }

    public async Task<TestAiModelConnectionResult> ExecuteAsync(AiModel input, CancellationToken cancellationToken = default)
    {
        var modelId = input.ModelId?.Trim() ?? string.Empty;
        if (modelId.Length == 0)
            return new TestAiModelConnectionResult(false, "Chưa nhập Model ID.");

        var endpoint = input.Endpoint?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new TestAiModelConnectionResult(false,
                "Endpoint không hợp lệ — cần URL đầy đủ, ví dụ http://127.0.0.1:1234/v1.");

        var apiKey = (input.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length == 0 && input.Id != Guid.Empty)
            apiKey = await _db.AiModels
                .Where(x => x.Id == input.Id)
                .Select(x => x.ApiKey)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        if (apiKey.Length == 0)
            return new TestAiModelConnectionResult(false,
                "Chưa có ApiKey để gọi thử. Endpoint local thường nhận giá trị bất kỳ (ví dụ lm-studio).");

        // Chỉ 3 trường quyết định "có gọi được không"; các cờ vision/structured-output không ảnh hưởng lời gọi thử.
        var probe = new AiModel { ModelId = modelId, Endpoint = endpoint, ApiKey = apiKey };
        var outcome = await _tester.TestAsync(probe, cancellationToken);

        return outcome.IsSuccess
            ? new TestAiModelConnectionResult(true, $"Kết nối OK — model trả lời sau {outcome.DurationMs} ms.", outcome.Detail)
            : new TestAiModelConnectionResult(false, $"Kết nối thất bại: {outcome.ErrorMessage}", outcome.Detail);
    }
}

/// <summary>Kết quả hiển thị trong modal: <paramref name="Message"/> là dòng chính, <paramref name="Detail"/> là phần phụ (có thể null).</summary>
public sealed record TestAiModelConnectionResult(bool Ok, string Message, string? Detail = null);
