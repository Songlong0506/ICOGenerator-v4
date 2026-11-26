using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Kết luận của nút Test Prompt Caching. Mỗi ca ứng với một việc người cấu hình phải làm khác nhau (giữ nguyên,
/// bật sticky session, tắt cache…), nên đọc nhầm ca này thành ca kia là khuyên sai.
/// </summary>
public class PromptCacheTesterTests
{
    private static readonly AiModel Vertex = new() { ModelId = "claude-sonnet-5-5", Endpoint = "https://aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic" };
    private static readonly AiModel OpenAi = new() { ModelId = "gpt-6-luna", Endpoint = "https://api.openai.com/v1" };

    private static PromptCacheProbe P(long? read, long? written, long? prompt = 6000) => new(prompt, read, written, 100);

    [Fact]
    public void LaterCallsAllRead_Works() =>
        Assert.Equal(PromptCacheVerdict.Works, PromptCacheTester.Judge(Vertex, [P(0, 6000), P(6000, 0), P(6000, 0)]));

    // Không sticky: lượt 2 rơi đúng backend đã ghi, lượt 3 thì không — trúng là do may, không phải "chạy được".
    [Fact]
    public void OnlySomeLaterCallsRead_IsIntermittent() =>
        Assert.Equal(PromptCacheVerdict.Intermittent, PromptCacheTester.Judge(Vertex, [P(0, 6000), P(6000, 0), P(0, 6000)]));

    // Đúng triệu chứng BMF round-robin trước sticky session: lượt nào cũng trả tiền ghi.
    [Fact]
    public void EveryCallWritesNoneReads_IsWrittenNotRead() =>
        Assert.Equal(PromptCacheVerdict.WrittenNotRead, PromptCacheTester.Judge(Vertex, [P(0, 6000), P(0, 6000), P(0, 6000)]));

    [Fact]
    public void NothingWrittenNothingRead_IsNotWritten() =>
        Assert.Equal(PromptCacheVerdict.NotWritten, PromptCacheTester.Judge(Vertex, [P(0, 0), P(0, 0), P(0, 0)]));

    // OpenAI không báo phần ghi (null) — không được kết luận "không ghi gì" như Anthropic.
    [Fact]
    public void OpenAiWithoutReads_IsNoHits_NotNotWritten() =>
        Assert.Equal(PromptCacheVerdict.NoHits, PromptCacheTester.Judge(OpenAi, [P(0, null), P(0, null), P(0, null)]));

    [Fact]
    public void OpenAiReads_Works() =>
        Assert.Equal(PromptCacheVerdict.Works, PromptCacheTester.Judge(OpenAi, [P(0, null), P(5888, null), P(5888, null)]));

    [Fact]
    public void CacheDisabledOnVertex_SaysSo_InsteadOfBlamingTheGateway()
    {
        var disabled = new AiModel { ModelId = Vertex.ModelId, Endpoint = Vertex.Endpoint, DisablePromptCache = true };
        Assert.Equal(PromptCacheVerdict.DisabledForModel, PromptCacheTester.Judge(disabled, [P(0, 0), P(0, 0), P(0, 0)]));
    }

    [Fact]
    public void NoUsageReported_IsNotReported() =>
        Assert.Equal(PromptCacheVerdict.NotReported, PromptCacheTester.Judge(Vertex, [P(null, null, null), P(null, null, null), P(null, null, null)]));
}
