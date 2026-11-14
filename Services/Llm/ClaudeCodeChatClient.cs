using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// <see cref="IChatClient"/> cho model Claude chạy qua <b>Claude Code CLI</b> (<c>claude -p</c>) đã đăng nhập
/// trên máy đang chạy app — tức là dùng gói Claude Pro/Max của người chạy máy, không cần API key. Model loại
/// này khai ở màn AI Models với Endpoint <c>claude-code://subscription</c>.
///
/// <para>
/// CLI chỉ được dùng làm ĐƯỜNG GỌI MODEL: mọi tool có sẵn của Claude Code bị tắt (<c>--tools ""</c>), system
/// prompt của nó bị thay bằng prompt của app, không nạp settings/skill/MCP nào. Vòng lặp agent, tool, chính
/// sách tool, cổng duyệt và call log vẫn là của app — model này gán cho agent như mọi model khác.
/// </para>
///
/// <para>
/// CLI in ra chính các sự kiện Anthropic Messages (bọc trong <c>stream_event</c>), nên phần đọc stream dùng
/// lại <see cref="AnthropicVertexChatClient.StreamState"/>. Ba chỗ phải dịch vì CLI không phải API:
/// </para>
/// <list type="bullet">
/// <item>Mỗi lời gọi là một phiên CLI mới nhận MỘT lượt user, nên lịch sử hội thoại được kể lại thành bản ghi
/// <c>&lt;turn role="…"&gt;</c> trong lượt đó (ảnh/PDF vẫn đi kèm dạng block).</item>
/// <item>CLI không nhận khai báo tool của bên ngoài, nên tool của agent đi theo giao thức chữ: danh sách tool
/// nằm trong system prompt, model gọi bằng khối <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> và client dịch
/// khối đó thành <see cref="FunctionCallContent"/>.</item>
/// <item>Structured output dùng <c>--json-schema</c> của CLI — nó tự ép model gọi tool <c>StructuredOutput</c>.</item>
/// </list>
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient
{
    /// <summary>Tên tool mà CLI tự khai cho <c>--json-schema</c>; input của nó là câu trả lời.</summary>
    public const string StructuredOutputToolName = "StructuredOutput";

    /// <summary>
    /// Tên gán cho một khối <c>tool_call</c> hỏng tới mức không đọc nổi tên tool. Không tool nào mang tên này,
    /// nên vòng lặp tool trả lỗi "không có tool" về cho model và model tự viết lại lời gọi.
    /// </summary>
    public const string InvalidToolCallName = "invalid_tool_call";

    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    private static readonly JsonSerializerOptions ArgumentJson = AIJsonUtilities.DefaultOptions;
    private static readonly Regex ToolNamePattern = new("\"name\"\\s*:\\s*\"([^\"\\\\]+)\"", RegexOptions.Compiled);

    private readonly AiModel _model;
    private readonly IClaudeCodeRunner _runner;
    private readonly ChatClientMetadata _metadata;

    public ClaudeCodeChatClient(AiModel model, IClaudeCodeRunner runner)
    {
        _model = model;
        _runner = runner;
        _metadata = new ChatClientMetadata("claude-code", OpenAiCompatibility.UriOf(model.Endpoint), model.ModelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var invocation = BuildInvocation(_model, messages.ToList(), options);
        var usesTools = AdvertisedFunctions(options).Count > 0;
        var state = new AnthropicVertexChatClient.StreamState(
            expectsResponseTool: ResponseSchema(options) is not null, StructuredOutputToolName);

        // Lượt có tool: chữ được GIỮ tới cuối lượt rồi mới tách phần lời và các khối tool_call — phát từng
        // token ra là UI hiện nguyên khối JSON của lời gọi tool.
        var held = new StringBuilder();
        var sawContent = false;

        await foreach (var line in _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false))
        {
            if (ParseLine(line) is not { } evt)
                continue;

            switch (evt["type"]?.GetValue<string>())
            {
                case "stream_event" when evt["event"] is JsonObject inner:
                    foreach (var update in state.Handle(inner))
                    {
                        if (!usesTools)
                        {
                            sawContent |= update.Contents.Any(c => c is TextContent);
                            yield return update;
                            continue;
                        }

                        foreach (var text in update.Contents.OfType<TextContent>())
                            held.Append(text.Text);
                        if (update.FinishReason is null)
                            continue;

                        // Cuối một message của model: tách chữ đã giữ, rồi phát kèm usage/finish của update này.
                        var contents = ParseToolCalls(held.ToString());
                        held.Clear();
                        sawContent |= contents.Count > 0;
                        var calls = contents.Any(c => c is FunctionCallContent);
                        var final = new ChatResponseUpdate(ChatRole.Assistant,
                            contents.Concat(update.Contents.Where(c => c is not TextContent)).ToList())
                        {
                            ResponseId = update.ResponseId,
                            MessageId = update.MessageId,
                            ModelId = update.ModelId,
                            FinishReason = calls ? ChatFinishReason.ToolCalls : update.FinishReason,
                        };
                        yield return final;
                    }
                    break;

                case "result":
                    var result = evt["result"] is JsonValue value && value.TryGetValue<string>(out var resultText) ? resultText : null;
                    if (evt["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError)
                        throw new InvalidOperationException(ErrorMessage(evt, result));
                    // CLI tự trả lời mà không gọi model (vd. thông báo cục bộ): không có stream_event nào.
                    if (!sawContent && !string.IsNullOrEmpty(result))
                        yield return new ChatResponseUpdate(ChatRole.Assistant, result) { FinishReason = ChatFinishReason.Stop };
                    break;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose() { }

    private static JsonObject? ParseLine(string line)
    {
        if (line.Length == 0 || line[0] != '{')
            return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string ErrorMessage(JsonObject evt, string? result)
    {
        var status = evt["api_error_status"] is JsonValue value && value.TryGetValue<int>(out var code) ? $" (HTTP {code})" : string.Empty;
        return $"Claude Code CLI báo lỗi{status}: {(string.IsNullOrWhiteSpace(result) ? "không có mô tả" : result)}";
    }

    // ── Request ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lời gọi CLI tương đương lời gọi M.E.AI. Internal để test đọc được trực tiếp.</summary>
    internal static ClaudeCodeInvocation BuildInvocation(AiModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var functions = AdvertisedFunctions(options);
        var conversation = messages.Where(m => m.Role != ChatRole.System).ToList();
        // Một lượt user trần thì đưa nguyên văn; có lịch sử (hoặc có tool) thì kể lại thành bản ghi.
        var transcript = conversation.Count != 1 || conversation[0].Role != ChatRole.User;

        var system = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            system.Add(options.Instructions);
        system.AddRange(messages.Where(m => m.Role == ChatRole.System && !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text));
        if (transcript)
            system.Add(TranscriptInstruction);
        if (functions.Count > 0)
            system.Add(ToolInstruction(functions, options!.ToolMode is RequiredChatToolMode));
        if (system.Count == 0)
            system.Add("Bạn là một trợ lý hữu ích.");

        var arguments = new List<string>
        {
            "-p",
            "--model", model.ModelId.Trim(),
            // Chỉ dùng CLI làm đường gọi model: không tool, không settings, không skill, không MCP, không lưu phiên.
            "--tools", "",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
        };
        if (ResponseSchema(options) is { } schema)
        {
            arguments.Add("--json-schema");
            arguments.Add(schema);
        }

        var content = transcript ? TranscriptBlocks(conversation) : ContentBlocks(conversation[0]);
        if (content.Count == 0)
            content.Add(TextBlock("Bắt đầu."));
        var stdin = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };

        return new ClaudeCodeInvocation(arguments, string.Join("\n\n", system), stdin.ToJsonString());
    }

    private const string TranscriptInstruction =
        "## Định dạng hội thoại\n"
        + "Tin nhắn của người dùng là BẢN GHI toàn bộ hội thoại tới lúc này, mỗi lượt nằm trong một thẻ "
        + "<turn role=\"user|assistant|tool\">. Hãy viết ĐÚNG MỘT lượt assistant kế tiếp: chỉ nội dung của lượt đó, "
        + "không bọc trong thẻ <turn>, không nhắc lại bản ghi.";

    private static string ToolInstruction(IReadOnlyList<AIFunctionDeclaration> functions, bool required)
    {
        var tools = new JsonArray(functions
            .Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["description"] = f.Description,
                ["input_schema"] = JsonNode.Parse(f.JsonSchema.GetRawText()),
            })
            .ToArray());

        return "## Gọi tool\n"
            + "Bạn làm việc bằng các tool liệt kê bên dưới. Muốn gọi tool, viết ở CUỐI lượt trả lời một hoặc nhiều khối "
            + "đúng dạng sau (mỗi lời gọi một khối):\n"
            + ToolCallOpen + "{\"name\":\"<tên tool>\",\"arguments\":{…}}" + ToolCallClose + "\n"
            + "- Bên trong khối là MỘT đối tượng JSON hợp lệ; `arguments` theo đúng `input_schema` của tool, chuỗi "
            + "phải escape đúng chuẩn JSON (xuống dòng là \\n, dấu ngoặc kép là \\\").\n"
            + "- Sau khối cuối cùng thì DỪNG: không viết gì thêm, không tự bịa kết quả. Kết quả thật sẽ đến ở lượt "
            + "kế tiếp trong thẻ <tool_result>.\n"
            + "- Chỉ gọi tool có trong danh sách. Khi đã xong việc và không cần tool nữa, trả lời bình thường, không "
            + "có khối " + ToolCallOpen + " nào.\n"
            + (required ? "- Lượt này BẮT BUỘC phải gọi ít nhất một tool.\n" : string.Empty)
            + "\nDanh sách tool (JSON):\n" + tools.ToJsonString();
    }

    /// <summary>
    /// JSON Schema cho <c>--json-schema</c>, hoặc null khi lượt này không ép được: không xin JSON, hoặc có tool
    /// thật (CLI ép tool câu trả lời thì model hết đường gọi tool của agent — parser tay của caller là lưới đỡ).
    /// Mức <c>JsonObject</c> (không schema) dùng <c>{"type":"object"}</c>.
    /// </summary>
    internal static string? ResponseSchema(ChatOptions? options)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson format || AdvertisedFunctions(options).Count > 0)
            return null;
        if (format.Schema is not { } schema)
            return "{\"type\":\"object\"}";
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject node)
            return null;
        node.Remove("$schema");
        return node.ToJsonString();
    }

    private static List<AIFunctionDeclaration> AdvertisedFunctions(ChatOptions? options) =>
        options is null || (options.ToolMode is NoneChatToolMode)
            ? []
            : options.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];

    private static JsonArray TranscriptBlocks(IReadOnlyList<ChatMessage> conversation)
    {
        var blocks = new JsonArray();
        foreach (var message in conversation)
        {
            var role = message.Role == ChatRole.Assistant ? "assistant" : message.Role == ChatRole.Tool ? "tool" : "user";
            AppendText(blocks, $"<turn role=\"{role}\">\n");
            foreach (var block in ContentBlocks(message).ToList())
            {
                block.Parent?.AsArray().Remove(block);
                if (block?["type"]?.GetValue<string>() == "text")
                    AppendText(blocks, block["text"]!.GetValue<string>() + "\n");
                else
                    blocks.Add(block);
            }
            AppendText(blocks, "</turn>\n");
        }
        return blocks;
    }

    // Gộp chữ liền nhau thành một block: bản ghi chỉ tách block ở chỗ có ảnh/tài liệu chen vào.
    private static void AppendText(JsonArray blocks, string text)
    {
        if (blocks.Count > 0 && blocks[^1] is JsonObject last && last["type"]?.GetValue<string>() == "text")
            last["text"] = last["text"]!.GetValue<string>() + text;
        else
            blocks.Add(TextBlock(text));
    }

    private static JsonArray ContentBlocks(ChatMessage message)
    {
        var blocks = new JsonArray();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent { Text: { Length: > 0 } text } when !string.IsNullOrWhiteSpace(text):
                    blocks.Add(TextBlock(text));
                    break;
                case DataContent data when data.HasTopLevelMediaType("image") || data.MediaType == "application/pdf":
                    blocks.Add(new JsonObject
                    {
                        ["type"] = data.MediaType == "application/pdf" ? "document" : "image",
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
                case FunctionCallContent call:
                    blocks.Add(TextBlock(ToolCallOpen + new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = JsonSerializer.SerializeToNode(
                            call.Arguments ?? new Dictionary<string, object?>(), ArgumentJson) ?? new JsonObject(),
                    }.ToJsonString() + ToolCallClose));
                    break;
                case FunctionResultContent result:
                    blocks.Add(TextBlock(
                        $"<tool_result id=\"{result.CallId}\"{(result.Exception is null ? string.Empty : " is_error=\"true\"")}>\n"
                        + ToolResultText(result) + "\n</tool_result>"));
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

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    // ── Response ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tách một lượt trả lời thành phần lời (trước khối đầu tiên) và các lời gọi tool. JSON của mỗi khối được
    /// đọc bằng parser thật từ sau thẻ mở — không dò thẻ đóng — nên nội dung file có chứa chữ
    /// <c>&lt;/tool_call&gt;</c>, hay model quên thẻ đóng, đều không làm lệch. Khối hỏng (JSON cụt vì hết
    /// token, sai cú pháp) vẫn thành một lời gọi: giữ tên tool nếu đọc được để rào chắn thiếu-tham-số của tool
    /// báo lại cho model, không thì mang tên <see cref="InvalidToolCallName"/>.
    /// </summary>
    internal static List<AIContent> ParseToolCalls(string text)
    {
        var contents = new List<AIContent>();
        var first = text.IndexOf(ToolCallOpen, StringComparison.Ordinal);
        var prose = (first < 0 ? text : text[..first]).Trim();
        if (prose.Length > 0)
            contents.Add(new TextContent(prose));

        var position = first;
        while (position >= 0)
        {
            var start = position + ToolCallOpen.Length;
            var bytes = Encoding.UTF8.GetBytes(text[start..]);
            var consumed = 0;
            JsonObject? call = null;
            try
            {
                var reader = new Utf8JsonReader(bytes);
                using var document = JsonDocument.ParseValue(ref reader);
                consumed = (int)reader.BytesConsumed;
                call = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
            }
            catch (JsonException) { /* xử lý bên dưới như khối hỏng */ }

            var name = call?["name"] is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;
            if (call is not null && !string.IsNullOrWhiteSpace(name))
            {
                var arguments = call["arguments"] is JsonObject args
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.ToJsonString(), ArgumentJson)
                    : null;
                contents.Add(new FunctionCallContent(NewCallId(), name, arguments ?? new Dictionary<string, object?>()));
            }
            else
            {
                var match = ToolNamePattern.Match(text, start);
                contents.Add(new FunctionCallContent(NewCallId(), match.Success ? match.Groups[1].Value : InvalidToolCallName,
                    new Dictionary<string, object?>()));
            }

            // Đi tiếp từ sau JSON vừa đọc (tính theo ký tự, vì consumed là byte UTF-8).
            var next = start + Encoding.UTF8.GetString(bytes, 0, consumed).Length;
            position = consumed == 0 ? -1 : text.IndexOf(ToolCallOpen, next, StringComparison.Ordinal);
        }
        return contents;
    }

    private static string NewCallId() => "call_" + Guid.NewGuid().ToString("N");
}
