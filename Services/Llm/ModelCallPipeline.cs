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
/// quên (đặc biệt phải <see cref="Reset"/> trước khi gọi lại), nên nó thuộc về đây chứ không phải từng caller.
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

    /// <summary>
    /// Client đã lắp middleware. Dùng trực tiếp khi cần một lời gọi KHÔNG stream — cụ thể là
    /// <c>GetResponseAsync&lt;T&gt;</c> (structured output mức json_schema) — rồi đọc <see cref="Result"/>.
    /// </summary>
    public IChatClient Client { get; }

    /// <summary>
    /// Kết quả của lời gọi vừa chạy qua đường ống này (thành công, hoặc thất bại đã được middleware nuốt).
    /// Ném nếu chưa có lời gọi nào hoàn tất — middleware LUÔN báo về, nên tình huống đó là lỗi lập trình.
    /// </summary>
    public LlmCallResult Result =>
        _captured ?? throw new InvalidOperationException("Model call produced no result.");

    /// <summary>Đã có kết quả chưa (đường json_schema kiểm tra trước khi thử lại).</summary>
    public bool HasResult => _captured != null;

    /// <summary>Quên kết quả cũ trước khi chạy lại lời gọi thứ hai trên cùng đường ống (vòng thử lại).</summary>
    public void Reset() => _captured = null;

    /// <summary>
    /// Chạy MỘT lời gọi dạng stream và gom toàn bộ nội dung vào <see cref="LlmCallResult"/> mà middleware
    /// dựng. <paramref name="onToken"/> nhận từng delta để UI hiện model "đang gõ" (SSE); sink UI hỏng
    /// tuyệt đối không được làm hỏng lời gọi nên mọi exception của nó bị nuốt.
    /// </summary>
    public async Task<LlmCallResult> StreamAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options, Action<string>? onToken, CancellationToken cancellationToken)
    {
        Reset();
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
