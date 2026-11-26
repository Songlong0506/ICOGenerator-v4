using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Application.Models;

/// <summary>
/// Nút "Test Prompt Caching" ở modal Add/Edit của trang Models: thử cache với cấu hình đang gõ (kể cả hai cờ
/// Disable prompt caching / Sticky session routing chưa lưu) và nói thẳng nên bật/tắt cờ nào. Trước đây câu trả
/// lời chỉ nằm trong file log máy chủ (dòng "Claude prompt cache …: X read, Y written"), người cấu hình không thấy.
/// Không ghi gì xuống DB.
/// </summary>
public class TestAiModelPromptCacheUseCase
{
    private readonly AppDbContext _db;
    private readonly IPromptCacheTester _tester;

    public TestAiModelPromptCacheUseCase(AppDbContext db, IPromptCacheTester tester)
    {
        _db = db;
        _tester = tester;
    }

    public async Task<TestAiModelPromptCacheResult> ExecuteAsync(AiModel input, CancellationToken cancellationToken = default)
    {
        var (probe, error) = await AiModelTestProbe.ResolveAsync(_db, input, cancellationToken);
        if (probe is null)
            return new TestAiModelPromptCacheResult(false, "error", error!);

        var outcome = await _tester.TestAsync(probe, cancellationToken);
        var lines = outcome.Probes.Select((p, i) => Loc.T("TestAiModelPromptCacheUseCase.Probe",
            i + 1, Count(p.PromptTokens), Count(p.ReadTokens), Count(p.WrittenTokens), p.DurationMs)).ToList();
        if (outcome.StickyInstance is { } instance)
            lines.Add(Loc.T("TestAiModelPromptCacheUseCase.Sticky", instance));
        var detail = lines.Count > 0 ? string.Join("\n", lines) : null;

        var (kind, message) = outcome.Verdict switch
        {
            PromptCacheVerdict.Works => ("success", Loc.T("TestAiModelPromptCacheUseCase.Works", PromptCacheTester.ProbeCount)),
            PromptCacheVerdict.Intermittent => ("warning", Loc.T("TestAiModelPromptCacheUseCase.Intermittent")),
            PromptCacheVerdict.WrittenNotRead => ("warning", Loc.T("TestAiModelPromptCacheUseCase.WrittenNotRead")),
            PromptCacheVerdict.NotWritten => ("warning", Loc.T("TestAiModelPromptCacheUseCase.NotWritten")),
            PromptCacheVerdict.NoHits => ("warning", Loc.T("TestAiModelPromptCacheUseCase.NoHits")),
            PromptCacheVerdict.DisabledForModel => ("info", Loc.T("TestAiModelPromptCacheUseCase.DisabledForModel")),
            PromptCacheVerdict.NotReported => ("warning", Loc.T("TestAiModelPromptCacheUseCase.NotReported")),
            _ => ("error", Loc.T("TestAiModelPromptCacheUseCase.Failed", outcome.Probes.Count + 1,
                outcome.HttpStatusCode is { } status ? $"HTTP {status} — {outcome.ErrorMessage}" : outcome.ErrorMessage)),
        };
        return new TestAiModelPromptCacheResult(outcome.Verdict == PromptCacheVerdict.Works, kind, message, detail);
    }

    // Null = endpoint không báo con số đó — hiện "n/a", không hiện 0 (hai chuyện khác nhau).
    private static string Count(long? value) => value is { } v ? v.ToString("N0") : Loc.T("TestAiModelPromptCacheUseCase.NotAvailable");
}

/// <summary>
/// Cùng dạng với <see cref="TestAiModelConnectionResult"/> để modal dùng chung một hàm hiển thị;
/// <paramref name="Kind"/> là lớp màu của hộp kết quả (<c>success</c>/<c>warning</c>/<c>info</c>/<c>error</c>).
/// </summary>
public sealed record TestAiModelPromptCacheResult(bool Ok, string Kind, string Message, string? Detail = null);
