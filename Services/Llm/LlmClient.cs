using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đường gọi LLM "một lượt hỏi–đáp" (BA chat, chắt lọc bộ nhớ, các cổng soát) — khác đường agent ở chỗ
/// KHÔNG có vòng lặp tool. Bản thân class chỉ còn phần điều phối; mọi việc nặng đã nằm ở chỗ khác:
/// <see cref="ModelCallPipeline"/> (dựng client + middleware + giữ kết quả), <see cref="EndpointQuirks"/>
/// (endpoint từ chối cái gì thì bỏ cái đó rồi thử lại) và <see cref="LlmJson"/> (đọc JSON model trả về).
/// </summary>
public class LlmClient : ILlmClient
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly IBudgetGuard _budgetGuard;
    private readonly LlmSettings _settings;
    private readonly ILogger<LlmClient> _logger;
    private readonly ModelCallImageStore? _imageStore;

    // imageStore không bắt buộc để các test dựng tay LlmClient khỏi phải quan tâm tới việc lưu ảnh; app
    // thật luôn có (DI). Đây là đường DUY NHẤT hiện gửi ảnh cho model — tài liệu nguồn của BA chat và ảnh
    // chụp POC của bước chấm giao diện đều đi qua ChatStructuredAsync ở đây.
    public LlmClient(IChatClientFactory chatClientFactory, IModelCallLogger modelCallLogger, IBudgetGuard budgetGuard, LlmSettings settings, ILogger<LlmClient> logger, ModelCallImageStore? imageStore = null)
    {
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _budgetGuard = budgetGuard;
        _settings = settings;
        _logger = logger;
        _imageStore = imageStore;
    }

    public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        StreamWithRetryAsync(model, messages, logContext, onToken, responseFormat: null, cancellationToken);

    public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
    {
        // response_format is opt-in per model via the Structured Output dropdown on the Models admin screen,
        // defaulting to None because many weak / local OpenAI-compatible servers reject the parameter. At every
        // level the caller keeps its own parser as a fallback, so a model returning JSON that misses the
        // expected shape still degrades gracefully instead of failing the turn.
        return model.StructuredOutputMode switch
        {
            StructuredOutputMode.JsonSchema => await ChatWithJsonSchemaAsync<T>(model, messages, logContext, onToken, cancellationToken).ConfigureAwait(false),
            StructuredOutputMode.JsonObject => await ChatWithJsonModeAsync<T>(model, messages, logContext, onToken, cancellationToken).ConfigureAwait(false),
            _ => (await ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).ConfigureAwait(false), null)
        };
    }

    // ── Đường stream (mọi mức đều đi đường này) ─────────────────────────────────────────────────────────

    // The streaming call plus the text-only-endpoint retry, shared by the plain chat path and both JSON levels
    // (which only differ by the response_format they ask for).
    private async Task<LlmCallResult> StreamWithRetryAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken)
    {
        var result = await StreamOnceAsync(model, messages, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);

        if (ShouldRetryWithoutImages(model, messages, result))
        {
            result = await StreamOnceAsync(model, EndpointQuirks.WithoutImageContent(messages), logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);
            result.ImagesDropped = true;
        }

        return result;
    }

    private Task<LlmCallResult> StreamOnceAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken) =>
        NewPipeline(model, logContext).StreamAsync(
            messages,
            new ChatOptions { ResponseFormat = responseFormat },
            onToken,
            cancellationToken);

    /// <summary>
    /// JSON mode: asks for <c>response_format: {"type":"json_object"}</c>, which guarantees syntactically valid
    /// JSON but says nothing about the shape — so the text is deserialized into <typeparamref name="T"/>
    /// leniently and a mismatch just yields a null value for the caller's own parser.
    /// </summary>
    private async Task<(LlmCallResult Result, T? Value)> ChatWithJsonModeAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class
    {
        // DeepSeek (and OpenAI's own json_object mode) reject the request unless the word "json" appears in the
        // prompt. Every prompt behind a structured call says it today, but prompts are edited by hand: skip the
        // parameter rather than spend a round trip on a guaranteed 400.
        if (!EndpointQuirks.MentionsJson(messages))
        {
            _logger.LogWarning(
                "Model {ModelId} đặt ở JSON mode nhưng prompt của lời gọi {Purpose} không chứa chữ \"json\" — "
                + "bỏ qua response_format cho lượt này. Hãy nhắc \"trả về JSON\" trong prompt để bật lại.",
                model.ModelId, logContext.Purpose);
            var plain = await ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, Deserialize<T>(plain));
        }

        return await StreamStructuredAsync<T>(model, StructuredOutputMode.JsonObject, ChatResponseFormat.Json, messages, logContext, onToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// json_schema: schema sinh từ <typeparamref name="T"/> — đúng schema mà <c>GetResponseAsync&lt;T&gt;</c>
    /// của Microsoft.Extensions.AI vẫn gửi, chỉ khác là đi đường STREAM.
    ///
    /// <para>
    /// Không dùng round-trip <c>GetResponseAsync&lt;T&gt;</c>, dù nó tiện hơn: một lời gọi không stream giữ kết
    /// nối IM LẶNG — chưa có cả header phản hồi — suốt thời gian model suy nghĩ và sinh, mà gateway/proxy đứng
    /// trước endpoint (Azure OpenAI farm, APIM, proxy công ty) cắt kết nối chờ quá ~30s. Kết quả là
    /// <c>HttpIOException: The response ended prematurely</c>, SDK tự thử lại 4 lần ra 4 lần chờ y hệt (hơn
    /// 2 phút cho một lượt hỏng) trong khi "Test Connection" vẫn xanh vì request "ping" xong trước ngưỡng cắt.
    /// Stream thì header về ngay và token chảy đều — và <paramref name="onToken"/> chạy được ở mức này như mọi
    /// mức khác.
    /// </para>
    /// </summary>
    private Task<(LlmCallResult Result, T? Value)> ChatWithJsonSchemaAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class =>
        StreamStructuredAsync<T>(model, StructuredOutputMode.JsonSchema, ChatResponseFormat.ForJsonSchema<T>(), messages, logContext, onToken, cancellationToken);

    // Phần chung của hai mức JSON: stream với response_format đang xin, endpoint không kham nổi mức đó thì
    // gọi lại một lần không kèm response_format (rồi để parser của caller lo), cuối cùng đọc text thành T.
    private async Task<(LlmCallResult Result, T? Value)> StreamStructuredAsync<T>(AiModel model, StructuredOutputMode mode, ChatResponseFormat responseFormat, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class
    {
        var result = await StreamWithRetryAsync(model, messages, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);

        // Endpoint turned out not to accept response_format at all (DeepSeek answers 400 "This response_format
        // type is unavailable now" to json_schema): retry once on the plain text path so a wrong Models-page
        // setting costs a round trip instead of the whole feature.
        if (ShouldRetryWithoutResponseFormat(model, mode, result, logContext.Purpose))
            result = await StreamWithRetryAsync(model, messages, logContext, onToken, responseFormat: null, cancellationToken).ConfigureAwait(false);

        // A 200 whose JSON doesn't fit T → null value → caller parses result.Content with its own parser.
        return (result, Deserialize<T>(result));
    }

    // ── Gửi lại KHÔNG kèm ảnh ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vì sao lượt CÓ ẢNH này đáng được gửi lại mà bỏ phần ảnh (null = không đáng). Hai lý do, và cả hai
    /// đều KHÔNG cứu được bằng cách thử lại y nguyên:
    /// <list type="bullet">
    ///   <item>endpoint từ chối thẳng content ảnh (model bị tick nhầm SupportsVision);</item>
    ///   <item>request chết ở tầng truyền tải, không có mã HTTP nào — với lượt mang ảnh thì gần như luôn là
    ///         body base64 quá lớn so với trần của gateway/proxy (xem
    ///         <see cref="EndpointQuirks.RequestNeverReachedModel"/>).</item>
    /// </list>
    /// Cả hai đều chỉ đáng thử lại khi lượt THẬT SỰ có ảnh — bỏ ảnh khỏi một lượt text thuần thì lần hai
    /// giống hệt lần một, chỉ tốn thêm một round-trip.
    /// </summary>
    private static string? ImageDropReason(IEnumerable<ChatMessage> messages, LlmCallResult result)
    {
        if (!EndpointQuirks.ContainsImageContent(messages))
            return null;
        if (EndpointQuirks.RejectedImageContent(result))
            return "vision";
        return EndpointQuirks.RequestNeverReachedModel(result) ? "transport" : null;
    }

    /// <summary>
    /// Có nên gọi lại mà BỎ <c>response_format</c> không. Hai hình dạng của cùng một chuyện "endpoint không
    /// kham nổi mức structured output đang xin":
    /// <list type="bullet">
    ///   <item>nó NÓI RA bằng 400 nêu đúng tên tham số (DeepSeek: "This response_format type is unavailable
    ///         now") — nhánh đã có từ trước;</item>
    ///   <item>nó ĐỨT GÁNH GIỮA CHỪNG: nhận request rồi đóng kết nối mà không trả xong phản hồi
    ///         (<c>HttpIOException: The response ended prematurely</c>). Xảy ra khi decoding bị ép theo
    ///         grammar/schema kéo dài quá timeout của gateway đứng trước endpoint, hoặc server tự chết vì
    ///         chính lượt sinh đó. Không có mã HTTP nào để mà đọc lý do, nên nếu chỉ bắt theo 400 thì lượt
    ///         này rơi thẳng thành bong bóng ⚠️.</item>
    /// </list>
    /// Cả hai đều chỉ còn một đường cứu: bỏ <c>response_format</c> gọi lại. (Ca gateway cắt kết nối IM LẶNG
    /// quá lâu thì đã được chặn từ gốc: mọi mức đều stream — xem <see cref="ChatWithJsonSchemaAsync{T}"/>.)
    /// </summary>
    private bool ShouldRetryWithoutResponseFormat(AiModel model, StructuredOutputMode mode, LlmCallResult result, string logContextPurpose)
    {
        if (EndpointQuirks.RejectedResponseFormat(result))
        {
            LogStructuredOutputUnsupported(model, mode, result);
            return true;
        }

        if (!EndpointQuirks.RequestNeverReachedModel(result))
            return false;

        _logger.LogWarning(
            "Lời gọi {Purpose} tới {ModelId} ở mức structured output {Mode} đứt giữa chừng (endpoint nhận "
            + "request rồi đóng kết nối, không có mã HTTP) — gọi lại KHÔNG kèm response_format, trên đường "
            + "stream. Nếu lỗi này chỉ xảy ra ở các lượt sinh câu trả lời DÀI, hãy hạ Structured Output của "
            + "model này ở trang Models. Lỗi: {Error}",
            logContextPurpose, model.ModelId, mode, result.ErrorMessage);
        return true;
    }

    private bool ShouldRetryWithoutImages(AiModel model, IEnumerable<ChatMessage> messages, LlmCallResult result)
    {
        switch (ImageDropReason(messages, result))
        {
            case "vision":
                LogVisionMisconfigured(model);
                return true;
            case "transport":
                LogPayloadUndeliverable(model, result);
                return true;
            default:
                return false;
        }
    }

    // ── Hạ tầng dùng chung ───────────────────────────────────────────────────────────────────────────

    // Mỗi lời gọi một đường ống mới: ModelCallLoggingChatClient đếm bước theo instance, nên lời gọi một
    // phát ăn ngay ở đây luôn là bước 1 — không lẫn số bước sang lời gọi kế tiếp.
    private ModelCallPipeline NewPipeline(AiModel model, ModelCallLogContext logContext) =>
        new(_chatClientFactory, model, _modelCallLogger, logContext,
            new ModelCallOptions(_settings.RequestTimeoutSeconds, ThrowOnFailure: false) { BudgetGuard = _budgetGuard },
            _imageStore);

    // Shared last step of the JSON levels: pull the object out of the reply text (tolerating a ```json fence
    // or chatter around it, which json_object already prevents but None/degraded replies don't) and read it
    // into T. Anything unparseable is a null value, i.e. "caller, use your own parser" — including a reply
    // whose field names are entirely foreign to T (requireKnownProperty), which would otherwise deserialize
    // into an all-defaults object and look like a parse success.
    private static T? Deserialize<T>(LlmCallResult result) where T : class =>
        result.IsSuccess ? LlmJson.TryDeserialize<T>(result.Content, requireKnownProperty: true) : null;

    private void LogStructuredOutputUnsupported(AiModel model, StructuredOutputMode mode, LlmCallResult result) =>
        _logger.LogWarning(
            "Model {ModelId} từ chối response_format mức {Mode} — gọi lại bằng đường text thuần và parse tay. "
            + "Hãy hạ mức Structured Output của model này ở trang Models để hết vòng gọi thừa. Lỗi: {Error}",
            model.ModelId, mode, result.ResponseText);

    private void LogPayloadUndeliverable(AiModel model, LlmCallResult result) =>
        _logger.LogWarning(
            "Lời gọi có ẢNH tới {ModelId} không tới được endpoint (lỗi tầng truyền tải, không có mã HTTP) — "
            + "gửi lại KHÔNG kèm ảnh. Ảnh đi trên dây dưới dạng base64 nên vài hình chụp màn hình đã đủ đẩy "
            + "request lên hàng chục MB; nếu lỗi này lặp lại, hạ Llm:SourceUpload:MaxTotalImageBytes / "
            + "MaxImagesPerCall cho khớp trần body của gateway trước endpoint. Lỗi: {Error}",
            model.ModelId, result.ErrorMessage);

    private void LogVisionMisconfigured(AiModel model) =>
        _logger.LogWarning(
            "Model {ModelId} từ chối content ảnh (image_url) dù SupportsVision đang bật — thử lại không kèm ảnh. Tắt SupportsVision cho model này ở trang Models để hết cảnh báo.",
            model.ModelId);
}

public class LlmCallResult
{
    public string Content { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public int PromptTokens { get; set; }

    /// <summary>
    /// Phần của <see cref="PromptTokens"/> mà provider phục vụ từ CACHE prompt (đã nằm trong PromptTokens,
    /// không cộng thêm). Chỉ có khi endpoint trả <c>prompt_tokens_details.cached_tokens</c> — server
    /// OpenAI-compatible/local không trả thì để 0. Đơn giá của phần này rẻ hơn hẳn phần input thường
    /// (OpenAI/DeepSeek: ~1/10), nên bỏ qua nó là báo chi phí đắt hơn hóa đơn thật — xem <see cref="LlmCost"/>.
    /// </summary>
    public int CachedPromptTokens { get; set; }

    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public long DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Lời gọi chết ở tầng TRUYỀN TẢI: request không hề tới được model (kết nối bị từ chối, bị đứt giữa lúc
    /// đẩy body, DNS/TLS hỏng) nên KHÔNG có mã HTTP nào. Khác hẳn "model/endpoint trả lỗi" — chỗ đó có
    /// <see cref="HttpStatusCode"/> và một thông điệp nói rõ tham số nào sai. Phân biệt được hai thứ này là
    /// điều kiện để <see cref="LlmClient"/> biết khi nào nên gửi lại lượt mà BỎ phần ảnh: body vài chục MB
    /// base64 bị proxy/gateway chặn cũng rơi vào đây, và đó là lượt duy nhất còn cứu được bằng cách bỏ ảnh.
    /// </summary>
    public bool TransportFailure { get; set; }

    /// <summary>Cỡ gói tin ước lượng của request (byte) — xem <c>ModelCallRequestPreview.ApproxBodyBytes</c>.</summary>
    public long ApproxRequestBytes { get; set; }

    /// <summary>
    /// Lượt này rốt cuộc đi ra KHÔNG kèm ảnh dù caller có gửi ảnh (endpoint từ chối content ảnh, hoặc body
    /// quá lớn nên phải gửi lại bản text). Caller PHẢI xem cờ này trước khi "khóa" nội dung hình thành chữ
    /// (<c>ProjectSourceFile.VisionSummary</c>): model chưa hề nhìn thấy tấm nào, mọi mô tả hình ở lượt này
    /// là bịa — khóa lại là mất vĩnh viễn đường nhìn lại ảnh.
    /// </summary>
    public bool ImagesDropped { get; set; }

    /// <summary>
    /// Các ẢNH thực sự đi kèm lượt gọi này, theo đúng thứ tự chúng nằm trong <see cref="RequestJson"/>.
    /// Rỗng ở tuyệt đại đa số lời gọi (chỉ chat BA có tài liệu nguồn và lượt chấm giao diện POC mới có ảnh).
    /// Bytes đi cùng kết quả tới <see cref="IModelCallLogger"/> để lưu ra đĩa rồi thôi — KHÔNG vào DB.
    /// </summary>
    public IReadOnlyList<ModelCallImage> RequestImages { get; set; } = Array.Empty<ModelCallImage>();
}
