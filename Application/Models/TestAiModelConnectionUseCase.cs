using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

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
        var (probe, error) = await AiModelTestProbe.ResolveAsync(_db, input, cancellationToken);
        if (probe is null)
            return new TestAiModelConnectionResult(false, error!);

        var outcome = await _tester.TestAsync(probe, cancellationToken);

        return outcome.IsSuccess
            ? new TestAiModelConnectionResult(true, Loc.T("TestAiModelConnectionUseCase.ExecuteAsync.4", outcome.DurationMs), outcome.Detail)
            : new TestAiModelConnectionResult(false, Loc.T("TestAiModelConnectionUseCase.ExecuteAsync.5", outcome.ErrorMessage), outcome.Detail);
    }
}

/// <summary>Kết quả hiển thị trong modal: <paramref name="Message"/> là dòng chính, <paramref name="Detail"/> là phần phụ (có thể null).</summary>
public sealed record TestAiModelConnectionResult(bool Ok, string Message, string? Detail = null);
