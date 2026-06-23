#if USE_MAF_SPIKE
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// SPIKE — middleware <see cref="IChatClient"/> ghi lại MỘT bản ghi call-log cho MỖI lời gọi model thật,
/// để đường MAF giữ được lịch sử call-log mà vòng lặp cũ ghi bằng tay từng bước. Đây chính là chi phí
/// "re-home vào middleware" nêu trong SPIKE-maf-agent.md: phần MAF KHÔNG cho miễn phí (~55 dòng).
///
/// Chèn BÊN TRONG FunctionInvokingChatClient để thấy từng call mỗi vòng tool. Map sang shape
/// <see cref="LlmCallResult"/> mà UI call-log đang dùng; các trường model do caller bù thêm.
/// </summary>
internal sealed class CallLoggingChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };
    private readonly Func<LlmCallResult, Task> _log;

    public CallLoggingChatClient(IChatClient inner, Func<LlmCallResult, Task> log) : base(inner) => _log = log;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        sw.Stop();
        await _log(BuildLog(messages, options, response, sw.ElapsedMilliseconds));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phải gom update để dựng bản ghi log sau khi stream xong, nhưng vẫn yield realtime để onToken sống.
        var sw = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }
        sw.Stop();
        await _log(BuildLog(messages, options, updates.ToChatResponse(), sw.ElapsedMilliseconds));
    }

    private static LlmCallResult BuildLog(IEnumerable<ChatMessage> messages, ChatOptions? options, ChatResponse response, long durationMs)
    {
        var promptTokens = TokenEstimator.Estimate(string.Join("\n", messages.Select(m => m.Text)));
        var text = response.Text ?? string.Empty;
        var completionTokens = TokenEstimator.Estimate(text);
        return new LlmCallResult
        {
            RequestJson = JsonSerializer.Serialize(new
            {
                messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }),
                temperature = options?.Temperature,
                max_tokens = options?.MaxOutputTokens,
                tools = options?.Tools?.Select(t => t.Name)
            }, SerializeOptions),
            ResponseText = text,
            ExtractedContent = text,
            Content = text,
            ErrorMessage = response.FinishReason == ChatFinishReason.Length
                ? "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length)."
                : null,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            DurationMs = durationMs,
            HttpStatusCode = 200,
            IsSuccess = true
        };
    }
}
#endif
