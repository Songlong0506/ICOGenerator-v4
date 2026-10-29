using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
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

    // Anthropic BẮT BUỘC max_tokens. Middleware luôn đặt MaxOutputTokens; số này chỉ là lưới đỡ cho caller
    // gọi thẳng client mà không đặt (vd. một test).
    private const int DefaultMaxTokens = 4096;

    private static readonly JsonSerializerOptions ArgumentJson = AIJsonUtilities.DefaultOptions;

    private readonly AiModel _model;
    private readonly ClientPipeline _pipeline;
    private readonly ChatClientMetadata _metadata;

    public AnthropicVertexChatClient(HttpClient http, AiModel model)
    {
        _model = model;
        _pipeline = ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            // Deadline duy nhất của một lời gọi do middleware lo — như OpenAIChatClientFactory.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            RetryPolicy = new LlmRetryPolicy(),
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
            ?? throw new ArgumentException($"Endpoint không phải URL hợp lệ: {model.Endpoint}");
        var basePath = PublisherBasePath(uri)
            ?? throw new ArgumentException($"Endpoint không có đoạn {PublisherSegment}: {model.Endpoint}");

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

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, options, stream: false);
        using var message = CreateMessage(RequestUri(_model, stream: false), body, bufferResponse: true, cancellationToken);
        await _pipeline.SendAsync(message).ConfigureAwait(false);
        var response = message.Response!;
        await ThrowIfErrorAsync(response).ConfigureAwait(false);

        return ParseResponse(JsonNode.Parse(response.Content.ToString())?.AsObject()
            ?? throw new InvalidOperationException("Anthropic trả về body rỗng."));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, options, stream: true);
        using var message = CreateMessage(RequestUri(_model, stream: true), body, bufferResponse: false, cancellationToken);
        await _pipeline.SendAsync(message).ConfigureAwait(false);
        var response = message.Response!;
        await ThrowIfErrorAsync(response).ConfigureAwait(false);

        var state = new StreamState();
        using var reader = new StreamReader(response.ContentStream!, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // SSE: dòng trống kết thúc một sự kiện; chỉ phần "data:" mang JSON (dòng "event:" lặp lại "type").
            if (line.Length == 0)
            {
                foreach (var update in state.Handle(data))
                    yield return update;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        foreach (var update in state.Handle(data))
            yield return update;
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
    internal static JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var systemParts = new List<string>();
        // Agent đưa system prompt qua ChatOptions.Instructions chứ không qua message System.
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            systemParts.Add(options.Instructions);
        var turns = new List<(string Role, JsonArray Blocks)>();
        var advertisesTools = AdvertisedFunctions(options).Count > 0;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    systemParts.Add(message.Text);
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
            turns.Insert(0, ("user", new JsonArray(TextBlock("Bắt đầu."))));

        // Prompt cache của Anthropic phải xin TƯỜNG MINH: điểm cắt cuối hội thoại để lượt sau đọc lại nguyên
        // phần đã có (chỗ tốn nhất của BA chat là prompt nền ~19k token gửi lại mỗi lượt).
        if (turns[^1].Blocks[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = Ephemeral();

        var body = new JsonObject
        {
            ["anthropic_version"] = AnthropicVersion,
            ["max_tokens"] = options?.MaxOutputTokens ?? DefaultMaxTokens,
        };

        var system = new JsonArray();
        if (systemParts.Count > 0)
        {
            var block = TextBlock(string.Join("\n\n", systemParts));
            block["cache_control"] = Ephemeral();
            system.Add(block);
        }
        // Chỉ dẫn JSON đứng SAU điểm cache của system: nó đổi theo từng loại lời gọi, còn prompt nền thì không.
        if (StructuredOutputInstruction(options?.ResponseFormat) is { } instruction)
            system.Add(TextBlock(instruction));
        if (system.Count > 0)
            body["system"] = system;

        body["messages"] = new JsonArray(turns
            .Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Blocks })
            .ToArray());

        if (stream)
            body["stream"] = true;

        AddTools(body, options);

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
    /// Anthropic không có <c>response_format</c>. Gửi một tham số lạ là 400 chắc chắn, nên mức structured
    /// output của model được dịch thành chỉ dẫn trong system prompt; parser tay của caller vẫn là lưới đỡ
    /// như ở mọi mức khác.
    /// </summary>
    internal static string? StructuredOutputInstruction(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: { } schema } =>
            "Chỉ trả lời bằng MỘT đối tượng JSON hợp lệ, không bọc trong ```, không thêm lời dẫn trước hay sau. "
            + "Đối tượng phải khớp JSON Schema sau:\n" + schema.GetRawText(),
        ChatResponseFormatJson =>
            "Chỉ trả lời bằng MỘT đối tượng JSON hợp lệ, không bọc trong ```, không thêm lời dẫn trước hay sau.",
        _ => null,
    };

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

    private static void AddTools(JsonObject body, ChatOptions? options)
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

        if (options!.ToolMode is RequiredChatToolMode required)
        {
            body["tool_choice"] = required.RequiredFunctionName is { } name
                ? new JsonObject { ["type"] = "tool", ["name"] = name }
                : new JsonObject { ["type"] = "any" };
        }
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    // ── Response ────────────────────────────────────────────────────────────────────────────────────────

    internal static ChatResponse ParseResponse(JsonObject body)
    {
        var contents = new List<AIContent>();
        foreach (var block in body["content"]?.AsArray() ?? new JsonArray())
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    contents.Add(new TextContent(block["text"]?.GetValue<string>() ?? string.Empty));
                    break;
                case "tool_use":
                    contents.Add(new FunctionCallContent(
                        block["id"]?.GetValue<string>() ?? string.Empty,
                        block["name"]?.GetValue<string>() ?? string.Empty,
                        ParseArguments(block["input"]?.ToJsonString())));
                    break;
            }
        }

        var id = body["id"]?.GetValue<string>();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents) { MessageId = id })
        {
            ResponseId = id,
            ModelId = body["model"]?.GetValue<string>(),
            FinishReason = FinishReason(body["stop_reason"]?.GetValue<string>()),
            Usage = Usage(body["usage"], outputTokens: null),
        };
    }

    private static IDictionary<string, object?> ParseArguments(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ArgumentJson) ?? new Dictionary<string, object?>();

    private static ChatFinishReason? FinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
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
        var input = Long(usage["input_tokens"]) + cacheRead + Long(usage["cache_creation_input_tokens"]);
        var output = outputTokens ?? Long(usage["output_tokens"]);
        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
            CachedInputTokenCount = cacheRead,
        };
    }

    private static long Long(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>Gom các sự kiện SSE của <c>:streamRawPredict</c> thành <see cref="ChatResponseUpdate"/>.</summary>
    internal sealed class StreamState
    {
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Json)> _toolBlocks = new();
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

        private IEnumerable<ChatResponseUpdate> Handle(JsonObject evt)
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
                    if (block?["type"]?.GetValue<string>() == "tool_use")
                        _toolBlocks[index] = (block["id"]?.GetValue<string>() ?? string.Empty,
                            block["name"]?.GetValue<string>() ?? string.Empty, new StringBuilder());
                    else if (block?["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { Length: > 0 } initial)
                        return [Update(new TextContent(initial))];
                    break;

                case "content_block_delta":
                    var delta = evt["delta"];
                    switch (delta?["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            return [Update(new TextContent(delta["text"]?.GetValue<string>() ?? string.Empty))];
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
                    final.FinishReason = FinishReason(evt["delta"]?["stop_reason"]?.GetValue<string>());
                    var outputTokens = evt["usage"]?["output_tokens"] is { } output ? Long(output) : (long?)null;
                    if (Usage(_startUsage ?? evt["usage"], outputTokens) is { } usage)
                        final.Contents.Add(new UsageContent(usage));
                    return [final];

                case "error":
                    var error = evt["error"];
                    throw new InvalidOperationException(
                        $"Anthropic stream error: {error?["type"]?.GetValue<string>()}: {error?["message"]?.GetValue<string>()}");
            }
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
