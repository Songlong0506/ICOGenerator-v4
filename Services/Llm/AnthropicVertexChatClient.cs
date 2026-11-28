using ICOGenerator.Localization;
using System.ClientModel;
using System.Collections.Concurrent;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// <see cref="IChatClient"/> cho model Claude chạy trên Vertex AI, gọi bằng API GỐC
/// <c>…/publishers/anthropic/models/{model}:rawPredict</c> (không stream) và <c>:streamRawPredict</c> (stream
/// SSE), body theo chuẩn Anthropic Messages (<c>anthropic_version: vertex-2023-10-16</c>).
///
/// <para>
/// Vì sao phải có class riêng: mọi đường khác trong app nói giao thức OpenAI <c>chat/completions</c>, mà Claude
/// trên Bosch Model Farm CHỈ có ở endpoint Vertex AI gốc (BMF Endpoint #2) — không có bản tương thích OpenAI
/// nào. Trỏ SDK OpenAI vào đó thì SDK nối <c>/chat/completions</c> sau URL và gateway trả 404. Class này dịch
/// qua lại giữa Microsoft.Extensions.AI và Anthropic Messages, nên mọi tầng phía trên (middleware log/budget,
/// vòng lặp tool của agent, <see cref="LlmClient"/>) không phải biết model đó là Claude.
/// </para>
///
/// <para>
/// Gửi đi bằng <see cref="ClientPipeline"/> của System.ClientModel — cùng transport (HttpClient đặt tên, chọn
/// proxy) và cùng <see cref="LlmRetryPolicy"/> như đường OpenAI, và lỗi HTTP ra đúng
/// <see cref="ClientResultException"/> nên <see cref="ModelCallLoggingChatClient"/> và
/// <see cref="ModelConnectionTester"/> đọc được mã lỗi mà không cần nhánh riêng.
/// </para>
/// </summary>
public sealed class AnthropicVertexChatClient : IChatClient
{
    /// <summary>Phiên bản API bắt buộc trong body khi gọi Claude qua Vertex AI.</summary>
    public const string AnthropicVersion = "vertex-2023-10-16";

    // Đoạn đường dẫn nhận diện endpoint Claude trên Vertex (gồm gateway BMF lẫn aiplatform.googleapis.com).
    private const string PublisherSegment = "/publishers/anthropic";

    /// <summary>
    /// Tên tool "câu trả lời" mà lời gọi structured output bị ÉP gọi (xem <see cref="ResponseToolSchema"/>).
    /// Input của tool chính là đối tượng JSON caller chờ; client trả nó về dưới dạng TEXT nên mọi tầng phía
    /// trên đọc như thể model tự viết JSON. Đặt tên riêng để không đụng tool nào của agent.
    /// </summary>
    public const string ResponseToolName = "structured_response";

    // Anthropic BẮT BUỘC max_tokens. Middleware luôn đặt MaxOutputTokens; số này chỉ là lưới đỡ cho caller
    // gọi thẳng client mà không đặt (vd. một test).
    private const int DefaultMaxTokens = 4096;

    private static readonly JsonSerializerOptions ArgumentJson = AIJsonUtilities.DefaultOptions;

    // Các (endpoint, model) đã từng 400 vì tool_choice ép tool — từ đó gửi sẵn tool_choice "auto", khỏi đốt
    // một lượt 400 ở MỖI lời gọi. Sống theo process, như danh sách reasoning_effort của
    // LlmRequestCompatibilityHandler: khởi động lại thì lượt đầu học lại.
    private static readonly ConcurrentDictionary<string, byte> RejectsForcedToolChoice = new(StringComparer.OrdinalIgnoreCase);

    // Cùng kiểu học-từ-lỗi cho eager_input_streaming: gateway đứng trước Vertex có thể chưa biết trường này.
    private static readonly ConcurrentDictionary<string, byte> RejectsEagerInputStreaming = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Khóa trong <see cref="UsageDetails.AdditionalCounts"/>: số token prompt lượt này GHI vào cache
    /// (<c>cache_creation_input_tokens</c>). Không có chỗ chuẩn trong M.E.AI, mà thiếu nó thì "cached 0" không
    /// phân biệt được "không ghi được cache" với "ghi rồi nhưng lượt sau không đọc lại được".
    /// <see cref="ModelCallLoggingChatClient"/> đọc cùng khóa này để ghi cột <c>CacheWritePromptTokens</c> của call log.
    /// </summary>
    public const string CacheCreationInputTokensKey = "CacheCreationInputTokens";

    /// <summary>
    /// Header sticky session của Bosch Model Farm: <c>01</c>/<c>02</c> chọn một trong hai backend cố định, thiếu
    /// header (hoặc giá trị khác, kể cả <c>1</c>/<c>2</c>) là round-robin. Bật theo model qua
    /// <see cref="AiModel.StickySessionRouting"/>.
    /// </summary>
    public const string StickySessionHeader = "x-bmf-sticky-session-instance";

    /// <summary>
    /// Instance cho một lời gọi, suy TẤT ĐỊNH từ khóa cache của project (<see cref="LlmCacheScope.KeyForProject"/>):
    /// mọi lời gọi của cùng một project về cùng một backend — đủ để prefix của project đó đọc lại được — còn các
    /// project khác nhau rải ra cả hai, nên không dồn toàn bộ tải (và rủi ro 429 BMF cảnh báo) vào một instance.
    /// Băm bằng SHA-256 chứ không <c>string.GetHashCode</c> (ngẫu nhiên theo process ⇒ đổi instance mỗi lần khởi
    /// động). Không có khóa (Test Connection) ⇒ <c>01</c>.
    /// </summary>
    public static string StickySessionInstance(string? cacheKey) =>
        string.IsNullOrEmpty(cacheKey) || (SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))[0] & 1) == 0 ? "01" : "02";

    private readonly AiModel _model;
    private readonly ClientPipeline _pipeline;
    private readonly ChatClientMetadata _metadata;
    private readonly ILogger? _logger;

    public AnthropicVertexChatClient(HttpClient http, AiModel model, ILoggerFactory? loggerFactory = null)
    {
        _model = model;
        _logger = loggerFactory?.CreateLogger<AnthropicVertexChatClient>();
        _pipeline = ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            // Deadline duy nhất của một lời gọi do middleware lo — như OpenAIChatClientFactory.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            RetryPolicy = new LlmRetryPolicy(loggerFactory?.CreateLogger<LlmRetryPolicy>()),
        });
        _metadata = new ChatClientMetadata("anthropic-vertex", OpenAiCompatibility.UriOf(model.Endpoint), model.ModelId);
    }

    /// <summary>
    /// True khi endpoint là Claude trên Vertex AI: đường dẫn có đoạn <c>/publishers/anthropic</c>, vd.
    /// <c>https://aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic</c>. Nhận theo đường dẫn chứ
    /// không theo host vì gateway nội bộ mỗi nơi một tên miền.
    /// </summary>
    public static bool IsAnthropicVertexEndpoint(string? endpoint) =>
        OpenAiCompatibility.UriOf(endpoint) is { } uri && PublisherBasePath(uri) is not null;

    /// <summary>
    /// URL thật của lời gọi. Mọi thứ SAU <c>/publishers/anthropic</c> trong ô Endpoint bị bỏ và dựng lại từ
    /// <see cref="AiModel.ModelId"/>, nên điền base (<c>…/publishers/anthropic</c>) hay dán nguyên URL có
    /// <c>/models/claude-…:generateContent</c> từ tài liệu đều ra cùng một chỗ — method <c>:generateContent</c>
    /// là của Gemini, Claude trả lỗi với nó.
    /// </summary>
    public static Uri RequestUri(AiModel model, bool stream)
    {
        var uri = OpenAiCompatibility.UriOf(model.Endpoint)
            ?? throw new ArgumentException(Loc.T("AnthropicVertexChatClient.RequestUri", model.Endpoint));
        var basePath = PublisherBasePath(uri)
            ?? throw new ArgumentException(Loc.T("AnthropicVertexChatClient.RequestUri.2", PublisherSegment, model.Endpoint));

        var method = stream ? "streamRawPredict" : "rawPredict";
        return new UriBuilder(uri)
        {
            Path = $"{basePath}/models/{model.ModelId.Trim()}:{method}",
            Query = string.Empty,
        }.Uri;
    }

    private static string? PublisherBasePath(Uri uri)
    {
        var path = uri.AbsolutePath;
        var index = path.IndexOf(PublisherSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var end = index + PublisherSegment.Length;
        // "/publishers/anthropicXYZ" không phải publisher anthropic.
        return end == path.Length || path[end] == '/' ? path[..end] : null;
    }

    /// <summary>
    /// False khi model này đã từng từ chối <c>tool_choice</c> ép tool (<c>tool</c>/<c>any</c>). Các model Claude
    /// đời mới (vd. <c>claude-sonnet-5-5</c>, <c>claude-opus-5-5</c>) trả 400 <i>"tool_choice: type "tool" and
    /// "any" are not supported for this model."</i>; đời cũ hơn (vd. <c>claude-haiku-5-5</c>) vẫn nhận. Không
    /// giữ danh sách tên model — học từ chính lỗi 400 đó, xem <see cref="SendAsync"/>.
    /// </summary>
    public static bool AcceptsForcedToolChoice(AiModel model) => !RejectsForcedToolChoice.ContainsKey(ForcedToolChoiceKey(model));

    private static string ForcedToolChoiceKey(AiModel model) => $"{model.Endpoint?.Trim()}|{model.ModelId?.Trim()}";

    internal static bool IsForcedToolChoiceRejection(int status, string? errorBody) =>
        status == 400 && errorBody is not null
        && errorBody.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
        && errorBody.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False khi endpoint/model này đã từng 400 vì trường <c>eager_input_streaming</c> trên tool (gateway hoặc
    /// bản phục vụ cũ chưa biết nó) — từ đó gửi tool không kèm trường này. Xem <see cref="BuildRequest"/>.
    /// </summary>
    public static bool AcceptsEagerInputStreaming(AiModel model) => !RejectsEagerInputStreaming.ContainsKey(ForcedToolChoiceKey(model));

    internal static bool IsEagerInputStreamingRejection(int status, string? errorBody) =>
        status == 400 && errorBody is not null
        && errorBody.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gửi request và trả message đã có phản hồi thành công (caller dispose). Hai kiểu 400 "không nhận tham số
    /// này" được tự gỡ: request ÉP tool mà model không nhận kiểu ép ⇒ gửi lại với <c>tool_choice: auto</c>;
    /// tool kèm <c>eager_input_streaming</c> mà endpoint không biết trường đó ⇒ gửi lại không kèm. Cả hai đều nhớ
    /// cặp (endpoint, model) để các lượt sau gửi sẵn — tầng log phía trên chỉ thấy lượt thành công.
    /// </summary>
    private async Task<PipelineMessage> SendAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream, CancellationToken cancellationToken)
    {
        var forced = AcceptsForcedToolChoice(_model);
        var eager = AcceptsEagerInputStreaming(_model);
        while (true)
        {
            var body = BuildRequest(messages, options, stream, forced, eager, promptCache: !_model.DisablePromptCache);
            var message = CreateMessage(RequestUri(_model, stream), body, bufferResponse: !stream, cancellationToken);
            try
            {
                await _pipeline.SendAsync(message).ConfigureAwait(false);
                var response = message.Response!;
                if (response.Status == 400 && (forced || eager))
                {
                    await response.BufferContentAsync().ConfigureAwait(false);
                    var error = response.Content.ToString();
                    if (forced && body.ContainsKey("tool_choice") && IsForcedToolChoiceRejection(response.Status, error))
                    {
                        RejectsForcedToolChoice[ForcedToolChoiceKey(_model)] = 0;
                        forced = false;
                        message.Dispose();
                        continue;
                    }
                    if (eager && IsEagerInputStreamingRejection(response.Status, error))
                    {
                        RejectsEagerInputStreaming[ForcedToolChoiceKey(_model)] = 0;
                        eager = false;
                        message.Dispose();
                        continue;
                    }
                }

                await ThrowIfErrorAsync(response).ConfigureAwait(false);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var message = await SendAsync(messages.ToList(), options, stream: false, cancellationToken).ConfigureAwait(false);
        var response = message.Response!;

        var parsed = ParseResponse(JsonNode.Parse(response.Content.ToString())?.AsObject()
            ?? throw new InvalidOperationException(Loc.T("AnthropicVertexChatClient.GetResponseAsync")));
        LogCacheUsage(parsed.Usage);
        return parsed;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = await SendAsync(messages.ToList(), options, stream: true, cancellationToken).ConfigureAwait(false);
        var response = message.Response!;

        var state = new StreamState(expectsResponseTool: ResponseToolSchema(options) is not null);
        using var reader = new StreamReader(response.ContentStream!, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // SSE: dòng trống kết thúc một sự kiện; chỉ phần "data:" mang JSON (dòng "event:" lặp lại "type").
            if (line.Length == 0)
            {
                foreach (var update in state.Handle(data))
                {
                    LogCacheUsage(update);
                    yield return update;
                }
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        foreach (var update in state.Handle(data))
        {
            LogCacheUsage(update);
            yield return update;
        }
    }

    private void LogCacheUsage(ChatResponseUpdate update)
    {
        foreach (var usage in update.Contents.OfType<UsageContent>())
            LogCacheUsage(usage.Details);
    }

    /// <summary>
    /// Một dòng log mỗi lời gọi: prompt đọc từ cache / ghi vào cache / tính đủ giá — cùng các số call log ghi theo
    /// từng dòng (<c>CachedPromptTokens</c>, <c>CacheWritePromptTokens</c>), nhưng ở đây đọc được liền một mạch qua
    /// nhiều lượt. Khi tỉ lệ trúng về 0, chuỗi này trả lời được vì sao: lượt nào cũng GHI mà lượt sau không
    /// đọc lại được ⇒ gateway rải request qua nhiều backend (cache Vertex sống theo từng region), không phải lỗi
    /// prefix; KHÔNG ghi gì ⇒ <c>cache_control</c> không tới được Anthropic hoặc prompt dưới ngưỡng tối thiểu.
    /// </summary>
    private void LogCacheUsage(UsageDetails? usage)
    {
        if (_logger is null || usage?.InputTokenCount is not { } prompt)
            return;

        var read = usage.CachedInputTokenCount ?? 0;
        var written = usage.AdditionalCounts?.TryGetValue(CacheCreationInputTokensKey, out var value) == true ? value : 0;
        _logger.LogInformation(
            "Claude prompt cache ({Model}): {Read} read, {Written} written, {Uncached} uncached of {Prompt} prompt tokens.",
            _model.ModelId, read, written, prompt - read - written, prompt);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose() { }

    // ── Request ─────────────────────────────────────────────────────────────────────────────────────────

    private PipelineMessage CreateMessage(Uri uri, JsonObject body, bool bufferResponse, CancellationToken cancellationToken)
    {
        var message = _pipeline.CreateMessage();
        message.Apply(new RequestOptions { CancellationToken = cancellationToken });
        message.BufferResponse = bufferResponse;

        var request = message.Request;
        request.Method = "POST";
        request.Uri = uri;
        // Gateway BMF và Vertex AI đều nhận khóa dạng Bearer.
        request.Headers.Set("Authorization", $"Bearer {_model.ApiKey}");
        if (_model.StickySessionRouting)
            request.Headers.Set(StickySessionHeader, StickySessionInstance(LlmCacheScope.CacheKey));
        request.Headers.Set("Content-Type", "application/json");
        request.Headers.Set("Accept", bufferResponse ? "application/json" : "text/event-stream");
        request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        return message;
    }

    private static async Task ThrowIfErrorAsync(PipelineResponse response)
    {
        if (!response.IsError)
            return;
        // Thông điệp mặc định của System.ClientModel chỉ có dòng trạng thái, nên tự ghép body lỗi vào: lời giải
        // thích của API ("model not found"…) là thứ EndpointQuirks và modal Test Connection đọc.
        await response.BufferContentAsync().ConfigureAwait(false);
        throw new ClientResultException(
            $"Service request failed.\nStatus: {response.Status} ({response.ReasonPhrase})\n\n{response.Content}", response);
    }

    /// <summary>Body Anthropic Messages tương đương lời gọi M.E.AI. Internal để test đọc được trực tiếp.</summary>
    /// <param name="forcedToolChoice">
    /// False cho model không nhận <c>tool_choice</c> ép tool (xem <see cref="AcceptsForcedToolChoice"/>): mọi chỗ
    /// lẽ ra ép thì gửi <c>auto</c>, việc "phải gọi tool" chỉ còn do chỉ dẫn trong system.
    /// </param>
    /// <param name="eagerInputStreaming">
    /// False cho endpoint đã từng 400 vì <c>eager_input_streaming</c> (xem <see cref="AcceptsEagerInputStreaming"/>).
    /// </param>
    /// <param name="promptCache">
    /// False khi model tắt prompt cache (<see cref="AiModel.DisablePromptCache"/>): không gắn <c>cache_control</c>
    /// ở đâu cả, nên không token nào bị tính giá GHI cache.
    /// </param>
    internal static JsonObject BuildRequest(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream, bool forcedToolChoice = true,
        bool eagerInputStreaming = true, bool promptCache = true)
    {
        var systemParts = new List<(string Text, bool Breakpoint)>();
        // Agent đưa system prompt qua ChatOptions.Instructions chứ không qua message System.
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            systemParts.Add((options.Instructions, false));
        var turns = new List<(string Role, JsonArray Blocks)>();
        var advertisesTools = AdvertisedFunctions(options).Count > 0;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    systemParts.Add((message.Text, message.IsCacheBreakpoint()));
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            var blocks = ContentBlocks(message, advertisesTools);
            if (blocks.Count == 0)
                continue;

            // Anthropic đòi user/assistant xen kẽ — kết quả tool (role Tool) và lượt user ngay sau nó gộp làm một.
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                // Một JsonNode chỉ có một cha: phải gỡ khỏi mảng cũ trước khi chuyển sang lượt trước.
                var moved = blocks.ToList();
                blocks.Clear();
                foreach (var block in moved)
                    turns[^1].Blocks.Add(block);
            }
            else
            {
                turns.Add((role, blocks));
            }
        }

        // Lượt đầu phải là user. Lịch sử chat BA có thể mở bằng lời chào của assistant.
        if (turns.Count == 0 || turns[0].Role != "user")
            turns.Insert(0, ("user", new JsonArray { TextBlock("Bắt đầu.") }));

        // Prompt cache của Anthropic phải xin TƯỜNG MINH: điểm cắt cuối hội thoại để lượt sau đọc lại nguyên
        // phần đã có (chỗ tốn nhất của BA chat là prompt nền ~19k token gửi lại mỗi lượt).
        if (promptCache && turns[^1].Blocks[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = Ephemeral();

        var body = new JsonObject
        {
            ["anthropic_version"] = AnthropicVersion,
            ["max_tokens"] = options?.MaxOutputTokens ?? DefaultMaxTokens,
        };

        // MỖI message System là MỘT khối riêng, không ghép thành một chuỗi. Anthropic chỉ cache ở RANH GIỚI
        // KHỐI và chỉ ở chỗ được xin: trước đây cả chục khối system của lượt chat BA bị nối thành một, điểm
        // cache đặt ở cuối — mà cuối khối là bản đồ bao phủ, đổi mỗi lượt — nên prefix KHÔNG BAO GIỜ trùng
        // giữa hai lượt và ~97k token prompt bị tính đủ giá (cộng phí GHI cache) ở mọi lượt. Log BAChat
        // 2026-11-20: prompt 96.843 token, cached 0.
        var system = new JsonArray();
        var breakpoints = promptCache ? SystemBreakpoints(systemParts) : [];
        for (var i = 0; i < systemParts.Count; i++)
        {
            var block = TextBlock(systemParts[i].Text);
            if (breakpoints.Contains(i))
                block["cache_control"] = Ephemeral();
            system.Add(block);
        }
        // Chỉ dẫn JSON đứng SAU điểm cache của system: nó đổi theo từng loại lời gọi, còn prompt nền thì không.
        if (StructuredOutputInstruction(options) is { } instruction)
            system.Add(TextBlock(instruction));
        if (system.Count > 0)
            body["system"] = system;

        body["messages"] = new JsonArray(turns
            .Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Blocks })
            .ToArray());

        if (stream)
            body["stream"] = true;

        AddTools(body, options, forcedToolChoice);
        AddResponseTool(body, options, forcedToolChoice);
        AddReasoning(body, options);

        // Mặc định Anthropic GOM trọn từng tham số của tool rồi mới phát input_json_delta. Tham số là cả một file
        // (SetPocContent mang trọn khung POC ~20–45k token) thì luồng SSE IM LẶNG suốt lúc model viết — vài phút
        // không một byte, gateway Model Farm cắt ở ~300s, thường trước cả header. Đây là lý do lượt Developer ĐẦU
        // TIÊN (lượt ghi khung POC) lần nào cũng chậm hoặc chết, còn lượt BA cùng độ dài thì không (câu trả lời
        // nhiều field nhỏ nên byte vẫn chảy). eager_input_streaming tắt việc gom: mẩu tham số chảy ngay khi sinh.
        // Đổi lại API không còn kiểm JSON của input — JSON hỏng đi thành đối số rỗng (ParseArguments) và
        // InvokerBackedAIFunction từ chối chạy tool thiếu đối số, model gọi lại. Lời gọi không stream thì trường
        // này vô nghĩa nên không gửi.
        if (stream && eagerInputStreaming && body["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>())
                tool["eager_input_streaming"] = true;
        }

        if (options?.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options?.TopP is { } topP)
            body["top_p"] = topP;
        if (options?.TopK is { } topK)
            body["top_k"] = topK;
        if (options?.StopSequences is { Count: > 0 } stops)
            body["stop_sequences"] = new JsonArray(stops.Select(s => (JsonNode)s).ToArray());

        return body;
    }

    /// <summary>
    /// Mức suy luận (<see cref="ChatOptions.Reasoning"/>, đặt từ <c>AiModel.ReasoningEffort</c>). Claude 5.x tự BẬT
    /// adaptive thinking khi không được bảo gì, và phần suy luận tính vào token output. <see cref="ReasoningEffort.None"/>
    /// ⇒ <c>thinking: disabled</c> (chỉ Haiku 5.5 nhận; Sonnet/Opus 5.5 trả 400 — lỗi nêu rõ, không đoán thay người
    /// cấu hình); các mức khác ⇒ <c>output_config.effort</c>. Không đặt ⇒ không gửi gì, như trước.
    /// </summary>
    internal static void AddReasoning(JsonObject body, ChatOptions? options)
    {
        switch (options?.Reasoning?.Effort)
        {
            case null:
                return;
            case ReasoningEffort.None:
                body["thinking"] = new JsonObject { ["type"] = "disabled" };
                return;
            case { } effort:
                body["output_config"] = new JsonObject { ["effort"] = AnthropicEffort(effort) };
                return;
        }
    }

    /// <summary>Tên mức effort của Anthropic (API và <c>claude --effort</c>) cho một mức M.E.AI khác None.</summary>
    internal static string AnthropicEffort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        _ => "xhigh",
    };

    /// <summary>
    /// Anthropic không có <c>response_format</c>. Gửi một tham số lạ là 400 chắc chắn, nên mức structured
    /// output của model được dịch sang công cụ Anthropic CÓ: ép gọi một tool (<see cref="ResponseToolName"/>)
    /// mà input_schema chính là schema của câu trả lời — xem <see cref="ResponseToolSchema"/>. Câu ở đây chỉ
    /// còn dặn model gọi tool đó; schema đã nằm trong tool nên không chép lại vào system.
    ///
    /// <para>
    /// Lượt có tool THẬT (vòng lặp agent) không ép được — ép tool câu trả lời thì model hết đường gọi tool
    /// của nó — nên lùi về chỉ dẫn "chỉ trả JSON" trong system, và parser tay của caller là lưới đỡ.
    /// </para>
    /// </summary>
    internal static string? StructuredOutputInstruction(ChatOptions? options)
    {
        if (ResponseToolSchema(options) is not null)
            return $"Trả lời bằng cách gọi tool `{ResponseToolName}` đúng MỘT lần: input của tool CHÍNH LÀ câu "
                + "trả lời — một đối tượng JSON theo đúng định dạng đã mô tả ở trên. Không viết chữ nào ngoài lời gọi tool.";

        return options?.ResponseFormat switch
        {
            ChatResponseFormatJson { Schema: { } schema } =>
                "Chỉ trả lời bằng MỘT đối tượng JSON hợp lệ, không bọc trong ```, không thêm lời dẫn trước hay sau. "
                + "Đối tượng phải khớp JSON Schema sau:\n" + schema.GetRawText(),
            ChatResponseFormatJson =>
                "Chỉ trả lời bằng MỘT đối tượng JSON hợp lệ, không bọc trong ```, không thêm lời dẫn trước hay sau.",
            _ => null,
        };
    }

    /// <summary>
    /// input_schema của tool câu trả lời, hoặc null khi lượt này không ép được.
    ///
    /// <para>
    /// Vì sao phải ép bằng tool: chỉ dẫn trong system là ràng buộc MỀM, và model nhỏ ở ngữ cảnh dài bỏ qua nó —
    /// ca thật: <c>claude-haiku-5-5</c>, lượt BAChat ~75k token ở mức JsonSchema, trả văn xuôi trần kèm thẻ
    /// điều khiển nội bộ <c>&lt;reasoning_effort&gt;0&lt;/reasoning_effort&gt;</c> lộ ra cuối câu. Với
    /// <c>tool_choice</c> ép đúng một tool, model không có đường trả văn xuôi — ngang hàng với
    /// <c>response_format: json_schema</c> của đường OpenAI.
    /// </para>
    ///
    /// <para>
    /// Mức <c>JsonObject</c> (không schema) dùng <c>{"type":"object"}</c>: bảo đảm một đối tượng JSON, hình dạng
    /// do prompt quyết — đúng ngữ nghĩa <c>json_object</c>. Schema gốc không phải object thì không ép được
    /// (Anthropic đòi input_schema là object).
    /// </para>
    /// </summary>
    internal static JsonObject? ResponseToolSchema(ChatOptions? options)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson format || AdvertisedFunctions(options).Count > 0)
            return null;

        if (format.Schema is not { } schema)
            return new JsonObject { ["type"] = "object" };

        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject node
            || node["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String || type.GetValue<string>() != "object")
            return null;

        // Từ khóa meta, không phải ràng buộc — bỏ cho body gọn.
        node.Remove("$schema");
        return node;
    }

    /// <param name="toolBlocks">
    /// False khi lượt này KHÔNG khai tool nào (lượt "chốt kết quả" của agent gửi <c>Tools = []</c> trên một
    /// lịch sử đầy tool): Anthropic trả 400 với block <c>tool_use</c>/<c>tool_result</c> khi request không có
    /// <c>tools</c>, nên các lượt gọi tool cũ được kể lại bằng chữ.
    /// </param>
    private static JsonArray ContentBlocks(ChatMessage message, bool toolBlocks)
    {
        var blocks = new JsonArray();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                // Anthropic từ chối block text rỗng.
                case TextContent { Text: { Length: > 0 } text } when !string.IsNullOrWhiteSpace(text):
                    blocks.Add(TextBlock(text));
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = data.MediaType,
                            ["data"] = Convert.ToBase64String(data.Data.Span),
                        },
                    });
                    break;
                case DataContent data when data.MediaType == "application/pdf":
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "document",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = data.MediaType,
                            ["data"] = Convert.ToBase64String(data.Data.Span),
                        },
                    });
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image"):
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "url", ["url"] = uri.Uri.ToString() },
                    });
                    break;
                case FunctionCallContent call when !toolBlocks:
                    blocks.Add(TextBlock($"[Đã gọi tool {call.Name}: {JsonSerializer.Serialize(call.Arguments, ArgumentJson)}]"));
                    break;
                case FunctionResultContent result when !toolBlocks:
                    blocks.Add(TextBlock($"[Kết quả tool: {ToolResultText(result)}]"));
                    break;
                case FunctionCallContent call when message.Role == ChatRole.Assistant:
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["input"] = JsonSerializer.SerializeToNode(
                            call.Arguments ?? new Dictionary<string, object?>(), ArgumentJson) ?? new JsonObject(),
                    });
                    break;
                case FunctionResultContent result:
                    var block = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.CallId,
                    };
                    if (ToolResultText(result) is { Length: > 0 } output)
                        block["content"] = output;
                    if (result.Exception is not null)
                        block["is_error"] = true;
                    blocks.Add(block);
                    break;
            }
        }
        return blocks;
    }

    private static string ToolResultText(FunctionResultContent result) => result.Result switch
    {
        null => result.Exception?.Message ?? string.Empty,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText(),
        var other => JsonSerializer.Serialize(other, ArgumentJson),
    };

    private static List<AIFunctionDeclaration> AdvertisedFunctions(ChatOptions? options) =>
        options is null || (options.ToolMode is NoneChatToolMode)
            ? []
            : options.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];

    private static void AddTools(JsonObject body, ChatOptions? options, bool forcedToolChoice)
    {
        var functions = AdvertisedFunctions(options);
        if (functions.Count == 0)
            return;

        body["tools"] = new JsonArray(functions
            .Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["description"] = f.Description,
                ["input_schema"] = JsonNode.Parse(f.JsonSchema.GetRawText()),
            })
            .ToArray());

        if (options!.ToolMode is RequiredChatToolMode required && forcedToolChoice)
        {
            body["tool_choice"] = required.RequiredFunctionName is { } name
                ? new JsonObject { ["type"] = "tool", ["name"] = name }
                : new JsonObject { ["type"] = "any" };
        }
    }

    private static void AddResponseTool(JsonObject body, ChatOptions? options, bool forcedToolChoice)
    {
        if (ResponseToolSchema(options) is not { } schema)
            return;

        body["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = ResponseToolName,
                ["description"] = "Gửi câu trả lời cuối cùng dưới dạng đối tượng JSON.",
                ["input_schema"] = schema,
            },
        };
        body["tool_choice"] = ResponseToolChoice(forcedToolChoice);
    }

    /// <summary>
    /// <c>tool_choice</c> của lượt structured output. Ép đúng tool câu trả lời khi model nhận; không thì
    /// <c>auto</c> — tool vẫn được khai và system vẫn dặn gọi nó, chỉ mất bảo đảm cứng. Tắt gọi song song để
    /// tối đa một lời gọi. Model trả văn xuôi thay vì gọi tool thì chữ đó đi lên như cũ và parser của caller đỡ.
    /// </summary>
    internal static JsonObject ResponseToolChoice(bool forcedToolChoice) => forcedToolChoice
        ? new JsonObject { ["type"] = "tool", ["name"] = ResponseToolName }
        : new JsonObject { ["type"] = "auto", ["disable_parallel_tool_use"] = true };

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    /// <summary>
    /// Anthropic nhận tối đa 4 điểm <c>cache_control</c> mỗi request; một điểm luôn dành cho lượt hội thoại
    /// cuối (xem <see cref="BuildRequest"/>), nên phần system được tối đa ba.
    /// </summary>
    internal const int MaxSystemBreakpoints = 3;

    /// <summary>
    /// Chỉ số các khối system được gắn <c>cache_control</c>. Caller đã đánh dấu khối nào kết thúc phần tĩnh
    /// (<see cref="PromptCacheHint"/>) thì theo đúng dấu đó. Không có dấu ⇒ khối ĐẦU (prompt nền, gần như
    /// luôn tĩnh) và khối CUỐI (giữ hành vi cũ cho caller có system tĩnh trọn vẹn, như agent). Quá trần thì
    /// giữ khối đầu tiên và các khối CUỐI — điểm càng muộn càng phủ được nhiều prefix.
    /// </summary>
    private static HashSet<int> SystemBreakpoints(IReadOnlyList<(string Text, bool Breakpoint)> parts)
    {
        if (parts.Count == 0)
            return new HashSet<int>();

        var marked = Enumerable.Range(0, parts.Count).Where(i => parts[i].Breakpoint).ToList();
        if (marked.Count == 0)
            marked = new List<int> { 0, parts.Count - 1 }.Distinct().ToList();

        if (marked.Count > MaxSystemBreakpoints)
            marked = marked.Take(1).Concat(marked.TakeLast(MaxSystemBreakpoints - 1)).ToList();

        return marked.ToHashSet();
    }

    // ── Response ────────────────────────────────────────────────────────────────────────────────────────

    internal static ChatResponse ParseResponse(JsonObject body)
    {
        var contents = new List<AIContent>();
        string? toolAnswer = null;
        foreach (var block in body["content"]?.AsArray() ?? new JsonArray())
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    contents.Add(new TextContent(block["text"]?.GetValue<string>() ?? string.Empty));
                    break;
                // Tool câu trả lời: input CHÍNH LÀ câu trả lời — trả về dạng text như model tự viết JSON.
                case "tool_use" when block["name"]?.GetValue<string>() == ResponseToolName:
                    toolAnswer ??= block["input"]?.ToJsonString() ?? "{}";
                    break;
                case "tool_use":
                    contents.Add(new FunctionCallContent(
                        block["id"]?.GetValue<string>() ?? string.Empty,
                        block["name"]?.GetValue<string>() ?? string.Empty,
                        ParseArguments(block["input"]?.ToJsonString())));
                    break;
            }
        }

        // Không bị ép (tool_choice auto) thì model có thể viết lời dẫn trước khi gọi tool — câu trả lời là input
        // của tool, lời dẫn bỏ đi để caller nhận JSON sạch.
        var answeredByTool = toolAnswer is not null;
        if (answeredByTool)
            contents = [new TextContent(toolAnswer)];

        var id = body["id"]?.GetValue<string>();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { MessageId = id })
        {
            ResponseId = id,
            ModelId = body["model"]?.GetValue<string>(),
            FinishReason = FinishReason(body["stop_reason"]?.GetValue<string>(), answeredByTool),
            Usage = Usage(body["usage"], outputTokens: null),
        };
    }

    // JSON hỏng (có thể xảy ra khi bật eager_input_streaming — API không còn kiểm input) KHÔNG được làm chết cả
    // lời gọi: trả đối số rỗng để lớp chặn "thiếu đối số bắt buộc" của InvokerBackedAIFunction báo lại cho model.
    private static IDictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ArgumentJson) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    // stop_reason "tool_use" của tool câu trả lời là một câu trả lời XONG, không phải lời xin chạy tool.
    private static ChatFinishReason? FinishReason(string? stopReason, bool answeredByTool) => stopReason switch
    {
        null => null,
        "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
        "tool_use" when answeredByTool => ChatFinishReason.Stop,
        "tool_use" => ChatFinishReason.ToolCalls,
        "refusal" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    /// <summary>
    /// <c>input_tokens</c> của Anthropic KHÔNG gồm phần đọc từ cache hay phần vừa ghi vào cache, còn app hiểu
    /// "input" là toàn bộ prompt (cached là tập con của nó — xem <c>CachedInputTokenCount</c>), nên cộng lại.
    /// </summary>
    private static UsageDetails? Usage(JsonNode? usage, long? outputTokens)
    {
        if (usage is null)
            return null;

        var cacheRead = Long(usage["cache_read_input_tokens"]);
        var cacheCreation = Long(usage["cache_creation_input_tokens"]);
        var input = Long(usage["input_tokens"]) + cacheRead + cacheCreation;
        var output = outputTokens ?? Long(usage["output_tokens"]);
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
            CachedInputTokenCount = cacheRead,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { [CacheCreationInputTokensKey] = cacheCreation },
        };
    }

    private static long Long(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>Gom các sự kiện SSE của <c>:streamRawPredict</c> thành <see cref="ChatResponseUpdate"/>.</summary>
    internal sealed class StreamState
    {
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Json)> _toolBlocks = new();
        // Index các block tool câu trả lời: input_json_delta của chúng phát thẳng thành text, nên "BA đang gõ"
        // vẫn chạy như khi model tự viết JSON.
        private readonly HashSet<int> _responseBlocks = new();
        // Lượt chờ tool câu trả lời mà không ép được (tool_choice auto): model có thể viết lời dẫn trước khi gọi
        // tool. Chữ đó được GIỮ lại thay vì phát ngay — tool được gọi thì bỏ (câu trả lời là input của tool),
        // model không gọi tool thì phát ra ở cuối như câu trả lời văn xuôi.
        private readonly bool _expectsResponseTool;
        private readonly StringBuilder _heldText = new();

        // Tên tool câu trả lời trên dây. Vertex dùng tool do app tự khai (ResponseToolName); Claude Code CLI
        // tự khai tool của nó cho --json-schema (xem ClaudeCodeChatClient) — cùng cơ chế, khác tên.
        private readonly string _responseToolName;

        public StreamState(bool expectsResponseTool = false, string responseToolName = ResponseToolName)
        {
            _expectsResponseTool = expectsResponseTool;
            _responseToolName = responseToolName;
        }
        private string? _responseId;
        private string? _modelId;
        private JsonNode? _startUsage;

        public IEnumerable<ChatResponseUpdate> Handle(StringBuilder data)
        {
            if (data.Length == 0)
                return [];

            var evt = JsonNode.Parse(data.ToString())?.AsObject();
            return evt is null ? [] : Handle(evt);
        }

        internal IEnumerable<ChatResponseUpdate> Handle(JsonObject evt)
        {
            switch (evt["type"]?.GetValue<string>())
            {
                case "message_start":
                    _responseId = evt["message"]?["id"]?.GetValue<string>();
                    _modelId = evt["message"]?["model"]?.GetValue<string>();
                    _startUsage = evt["message"]?["usage"]?.DeepClone();
                    break;

                case "content_block_start":
                    var block = evt["content_block"];
                    var index = evt["index"]?.GetValue<int>() ?? 0;
                    if (block?["type"]?.GetValue<string>() == "tool_use" && block["name"]?.GetValue<string>() == _responseToolName)
                    {
                        _responseBlocks.Add(index);
                        _heldText.Clear();
                    }
                    else if (block?["type"]?.GetValue<string>() == "tool_use")
                        _toolBlocks[index] = (block["id"]?.GetValue<string>() ?? string.Empty,
                            block["name"]?.GetValue<string>() ?? string.Empty, new StringBuilder());
                    else if (block?["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { Length: > 0 } initial)
                        return Text(initial);
                    break;

                case "content_block_delta":
                    var delta = evt["delta"];
                    switch (delta?["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            return Text(delta["text"]?.GetValue<string>() ?? string.Empty);
                        case "input_json_delta" when _responseBlocks.Contains(evt["index"]?.GetValue<int>() ?? 0):
                            return delta["partial_json"]?.GetValue<string>() is { Length: > 0 } partial
                                ? [Update(new TextContent(partial))]
                                : [];
                        case "input_json_delta" when _toolBlocks.TryGetValue(evt["index"]?.GetValue<int>() ?? 0, out var tool):
                            tool.Json.Append(delta["partial_json"]?.GetValue<string>());
                            break;
                    }
                    break;

                case "content_block_stop":
                    if (_toolBlocks.Remove(evt["index"]?.GetValue<int>() ?? 0, out var done))
                        return [Update(new FunctionCallContent(done.Id, done.Name, ParseArguments(done.Json.ToString())))];
                    break;

                case "message_delta":
                    // output_tokens ở đây là số CỘNG DỒN cuối cùng; phần input chỉ có ở message_start.
                    var final = Update();
                    final.FinishReason = FinishReason(evt["delta"]?["stop_reason"]?.GetValue<string>(), _responseBlocks.Count > 0);
                    var outputTokens = evt["usage"]?["output_tokens"] is { } output ? Long(output) : (long?)null;
                    if (Usage(_startUsage ?? evt["usage"], outputTokens) is { } usage)
                        final.Contents.Add(new UsageContent(usage));
                    if (_heldText.Length == 0)
                        return [final];
                    var held = Update(new TextContent(_heldText.ToString()));
                    _heldText.Clear();
                    return [held, final];

                case "error":
                    var error = evt["error"];
                    throw new InvalidOperationException(
                        $"Anthropic stream error: {error?["type"]?.GetValue<string>()}: {error?["message"]?.GetValue<string>()}");
            }
            return [];
        }

        private IEnumerable<ChatResponseUpdate> Text(string text)
        {
            if (!_expectsResponseTool)
                return [Update(new TextContent(text))];
            // Tool câu trả lời đã mở thì mọi chữ ngoài nó là thừa.
            if (_responseBlocks.Count == 0)
                _heldText.Append(text);
            return [];
        }

        private ChatResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, contents.ToList())
        {
            ResponseId = _responseId,
            MessageId = _responseId,
            ModelId = _modelId,
        };
    }
}
