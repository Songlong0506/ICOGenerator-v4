using System.ClientModel;
using System.Diagnostics;
using System.Text;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Nút "Test Prompt Caching" ở trang Models: bắn <see cref="ProbeCount"/> lời gọi GIỐNG HỆT nhau, mang chung một
/// prefix tĩnh đủ dài để được cache, rồi đọc số token mỗi lượt GHI / ĐỌC cache. Trả lời được câu mà trước đây chỉ
/// file log máy chủ trả lời được: với cấu hình đang gõ (kể cả cờ Disable prompt caching / Sticky session chưa lưu),
/// cache có thực sự được đọc lại không.
///
/// <para>
/// Cùng đường dây thật như <see cref="ModelConnectionTester"/> (cùng <see cref="IChatClientFactory"/>, nên cùng
/// proxy, cùng shim request, cùng <c>cache_control</c>/header sticky của client Vertex) và cũng KHÔNG qua middleware
/// log/budget. Prefix mở đầu bằng một nonce mới mỗi lần bấm, nên lượt 1 luôn là một lần GHI sạch chứ không đọc
/// nhầm cache của lần bấm trước. Ba lượt chứ không hai: không có sticky session, một lượt đọc trúng có thể chỉ là
/// may (round-robin rơi đúng backend), nên phải thấy được ca "lúc trúng lúc trượt".
/// </para>
/// </summary>
public sealed class PromptCacheTester : IPromptCacheTester
{
    public const int ProbeCount = 3;

    /// <summary>
    /// Khóa định tuyến của lời gọi thử — chỗ client Vertex chọn instance sticky và đường OpenAI đặt
    /// <c>prompt_cache_key</c>. Cố định để ba lượt đi cùng một chỗ.
    /// </summary>
    public const string ProbeCacheKey = "ico-cache-test";

    // Ngưỡng prefix tối thiểu để được cache là 512–4096 token tùy model; ~6k chữ-token vượt mọi ngưỡng mà ba
    // lượt cộng lại vẫn dưới ~20k token input (vài cent với model đắt nhất đang khai).
    private const int PrefixLines = 600;
    private const int MaxOutputTokens = 100;

    private readonly IChatClientFactory _chatClientFactory;
    private readonly int _timeoutSeconds;

    public PromptCacheTester(IChatClientFactory chatClientFactory, LlmSettings settings)
    {
        _chatClientFactory = chatClientFactory;
        _timeoutSeconds = settings.TestConnectionTimeoutSeconds;
    }

    public async Task<PromptCacheTestOutcome> TestAsync(AiModel model, CancellationToken cancellationToken = default)
    {
        // AsyncLocal đặt trong method async chỉ chảy xuống các lời gọi bên dưới, không rò ra caller.
        LlmCacheScope.CacheKey = ProbeCacheKey;
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, Prefix(Guid.NewGuid())).AsCacheBreakpoint(),
            new(ChatRole.User, "Reply with the single word OK."),
        };
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };
        var probes = new List<PromptCacheProbe>();

        try
        {
            var client = _chatClientFactory.Create(model);
            for (var i = 0; i < ProbeCount; i++)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var stopwatch = Stopwatch.StartNew();
                var response = await client.GetResponseAsync(messages, options, linkedCts.Token).ConfigureAwait(false);
                probes.Add(Probe(response.Usage, stopwatch.ElapsedMilliseconds));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var status = LlmExceptionDetail.Unwrap(ex) is ClientResultException { Status: > 0 } api ? api.Status : (int?)null;
            return new PromptCacheTestOutcome(PromptCacheVerdict.Failed, probes, StickyInstance(model),
                LlmExceptionDetail.Describe(ex), status);
        }

        return new PromptCacheTestOutcome(Judge(model, probes), probes, StickyInstance(model));
    }

    /// <summary>Kết luận từ ba lượt. Internal để test kiểm từng ca mà không cần endpoint.</summary>
    internal static PromptCacheVerdict Judge(AiModel model, IReadOnlyList<PromptCacheProbe> probes)
    {
        if (probes.All(p => p.PromptTokens is null))
            return PromptCacheVerdict.NotReported;

        // Lượt 1 là lần ghi; chỉ các lượt sau mới nói được cache có được đọc lại không.
        var reads = probes.Skip(1).Select(p => (p.ReadTokens ?? 0) > 0).ToList();
        if (reads.All(hit => hit))
            return PromptCacheVerdict.Works;
        if (reads.Any(hit => hit))
            return PromptCacheVerdict.Intermittent;

        if (model.DisablePromptCache && AnthropicVertexChatClient.IsAnthropicVertexEndpoint(model.Endpoint))
            return PromptCacheVerdict.DisabledForModel;

        // Chỉ Anthropic báo phần GHI; OpenAI tự cache nên không có con số đó (null ≠ 0).
        var written = probes.Select(p => p.WrittenTokens).ToList();
        if (written.Any(w => w > 0))
            return PromptCacheVerdict.WrittenNotRead;
        return written.All(w => w == 0) ? PromptCacheVerdict.NotWritten : PromptCacheVerdict.NoHits;
    }

    private static PromptCacheProbe Probe(UsageDetails? usage, long durationMs)
    {
        long? written = usage?.AdditionalCounts?.TryGetValue(AnthropicVertexChatClient.CacheCreationInputTokensKey, out var value) == true
            ? value
            : null;
        return new PromptCacheProbe(usage?.InputTokenCount, usage?.CachedInputTokenCount, written, durationMs);
    }

    private static string? StickyInstance(AiModel model) =>
        model.StickySessionRouting && AnthropicVertexChatClient.IsAnthropicVertexEndpoint(model.Endpoint)
            ? AnthropicVertexChatClient.StickySessionInstance(ProbeCacheKey)
            : null;

    // Nội dung vô nghĩa nhưng tất định; nonce đứng ĐẦU vì cache khớp theo prefix — đứng cuối thì lần bấm sau vẫn
    // đọc trúng gần hết prefix của lần trước.
    private static string Prefix(Guid nonce)
    {
        var text = new StringBuilder($"Prompt cache probe {nonce:N}. The reference table below is static filler.\n");
        for (var i = 1; i <= PrefixLines; i++)
            text.Append("Row ").Append(i).Append(": alpha bravo charlie delta echo foxtrot golf hotel.\n");
        return text.ToString();
    }
}
