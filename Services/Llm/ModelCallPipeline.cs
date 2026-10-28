using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Một đường ống lời gọi model đã lắp sẵn: <see cref="IChatClient"/> theo <see cref="AiModel"/> (từ
/// <see cref="IChatClientFactory"/>) + middleware dùng chung <see cref="ModelCallLoggingChatClient"/>,
/// kèm việc GIỮ LẠI <see cref="LlmCallResult"/> mà middleware dựng ra.
///
/// Sinh ra để xóa đúng một đoạn từng chép ở bốn chỗ (<c>LlmClient</c> hai đường,
/// <c>EvalRunnerService</c> hai hàm): "dựng builder → <c>.Use(new ModelCallLoggingChatClient(…, onCompleted:
/// r => captured = r))</c> → chạy → <c>captured ?? throw</c>". Biến closure <c>captured</c> là chi tiết dễ
/// quên (đặc biệt phải xóa kết quả cũ trước khi gọi lại), nên nó thuộc về đây chứ không phải từng caller.
///
/// KHÔNG dùng cho đường agent: <c>AgentRunService</c> cần chính instance middleware (để đọc
/// <see cref="ModelCallLoggingChatClient.StepCount"/> và đổi trần vòng lặp giữa các pha) nên nó tự dựng.
/// </summary>
public sealed class ModelCallPipeline
{
    private LlmCallResult? _captured;

    public ModelCallPipeline(IChatClientFactory chatClientFactory, AiModel model, IModelCallLogger logger,
        ModelCallLogContext context, ModelCallOptions options, ModelCallImageStore? imageStore = null)
    {
        // Chat client bọc HttpClient gộp sẵn (do IHttpClientFactory quản), nên client OpenAI cố tình nhẹ và
        // dựng theo từng lời gọi; middleware dùng chung được chồng lên qua ChatClientBuilder.
        Client = chatClientFactory.Create(model)
            .AsBuilder()
            .Use(inner => new ModelCallLoggingChatClient(
                inner, model, logger, context, options with { OnCompleted = r => _captured = r }, imageStore))
            .Build();
    }

    /// <summary>Client đã lắp middleware.</summary>
    private IChatClient Client { get; }

    /// <summary>
    /// Kết quả của lời gọi vừa chạy qua đường ống này (thành công, hoặc thất bại đã được middleware nuốt).
    /// Ném nếu chưa có lời gọi nào hoàn tất — middleware LUÔN báo về, nên tình huống đó là lỗi lập trình.
    /// </summary>
    public LlmCallResult Result =>
        _captured ?? throw new InvalidOperationException("Model call produced no result.");

    /// <summary>
    /// Chạy MỘT lời gọi dạng stream và gom toàn bộ nội dung vào <see cref="LlmCallResult"/> mà middleware
    /// dựng. <paramref name="onToken"/> nhận từng delta để UI hiện model "đang gõ" (SSE); sink UI hỏng
    /// tuyệt đối không được làm hỏng lời gọi nên mọi exception của nó bị nuốt.
    /// </summary>
    public async Task<LlmCallResult> StreamAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options, Action<string>? onToken, CancellationToken cancellationToken)
    {
        _captured = null;
        await foreach (var update in Client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            var text = update.Text;
            if (string.IsNullOrEmpty(text) || onToken == null)
                continue;

            try { onToken(text); }
            catch { /* ignore UI streaming failures */ }
        }

        return Result;
    }
}
