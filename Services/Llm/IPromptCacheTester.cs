using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Thử prompt cache của một cấu hình <see cref="AiModel"/> (nút "Test Prompt Caching" ở trang Models). Như
/// <see cref="IModelConnectionTester"/>: KHÔNG ghi call log, KHÔNG tính vào budget, không cần project/agent.
/// </summary>
public interface IPromptCacheTester
{
    Task<PromptCacheTestOutcome> TestAsync(AiModel model, CancellationToken cancellationToken = default);
}

/// <summary>
/// Một lượt thử. Null = endpoint không báo con số đó (OpenAI không báo phần GHI; server không trả <c>usage</c>
/// thì cả ba đều null) — khác hẳn 0.
/// </summary>
public sealed record PromptCacheProbe(long? PromptTokens, long? ReadTokens, long? WrittenTokens, long DurationMs);

public enum PromptCacheVerdict
{
    /// <summary>Mọi lượt sau lượt đầu đều đọc được cache.</summary>
    Works,
    /// <summary>Lúc đọc được lúc không — request đang bị chia round-robin.</summary>
    Intermittent,
    /// <summary>Lượt nào cũng ghi, không lượt nào đọc lại — gateway không trả cache về.</summary>
    WrittenNotRead,
    /// <summary>Endpoint báo rõ không ghi gì — <c>cache_control</c> không tới nơi.</summary>
    NotWritten,
    /// <summary>Không lượt nào đọc được và endpoint không báo phần ghi (OpenAI).</summary>
    NoHits,
    /// <summary>Model đang bật Disable prompt caching nên không xin cache.</summary>
    DisabledForModel,
    /// <summary>Endpoint không trả <c>usage</c> — không kết luận được.</summary>
    NotReported,
    /// <summary>Một lượt gọi thất bại.</summary>
    Failed,
}

/// <param name="StickyInstance">Instance BMF đã ghim (<c>01</c>/<c>02</c>), null khi không bật sticky session.</param>
public sealed record PromptCacheTestOutcome(
    PromptCacheVerdict Verdict,
    IReadOnlyList<PromptCacheProbe> Probes,
    string? StickyInstance,
    string? ErrorMessage = null,
    int? HttpStatusCode = null);
