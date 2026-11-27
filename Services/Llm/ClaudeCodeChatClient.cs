using ICOGenerator.Localization;
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

    // Dạng gọi tool XML mà model Claude tự rơi về khi tham số là văn bản dài (HTML/JS): xem ParseToolCalls.
    private static readonly Regex InvokeOpenPattern = new("<invoke name=\"([^\"]+)\">", RegexOptions.Compiled);
    private static readonly Regex InvokeParameterPattern = new(
        "<parameter name=\"([^\"]+)\">(.*?)</parameter>\\s*(?=<parameter name=\"|\\z)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex InvokeTailPattern = new(
        "(\\s*</invoke>|\\s*</function_calls>)+\\s*\\z", RegexOptions.Compiled);
    private static readonly Regex InvokeWrapperPattern = new("\\s*<function_calls>\\s*\\z", RegexOptions.Compiled);

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
        var all = messages.ToList();
        var invocation = BuildInvocation(_model, all, options);
        var functions = AdvertisedFunctions(options);
        var usesTools = functions.Count > 0;
        var state = new AnthropicVertexChatClient.StreamState(
            expectsResponseTool: ResponseSchema(options) is not null, StructuredOutputToolName);

        // Lượt có tool (vòng lặp agent) chạy trên một PHIÊN GIỮ SỐNG — xem SessionTurn. Lượt không tool (BA
        // chat, structured output) là một phát: không có lượt sau nào để đọc lại cache.
        var pool = usesTools ? _runner.Sessions : null;
        var turn = pool is null ? null : SessionTurn.Begin(pool, _model, all, options, invocation);
        List<AIContent>? calledTools = null;

        // Lượt có tool: chữ được GIỮ tới cuối lượt rồi mới tách phần lời và các khối tool_call — phát từng
        // token ra là UI hiện nguyên khối JSON của lời gọi tool.
        var held = new StringBuilder();
        var sawContent = false;

        var lines = turn is null
            ? _runner.RunAsync(invocation, cancellationToken)
            : turn.Session.SendAsync(turn.StdinLine, cancellationToken);
        try
        {
            await foreach (var line in lines.ConfigureAwait(false))
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
                            var contents = ParseToolCalls(held.ToString(), functions);
                            held.Clear();
                            sawContent |= contents.Count > 0;
                            var calls = contents.Any(c => c is FunctionCallContent);
                            calledTools = calls ? contents : null;
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
        finally
        {
            // Lượt kết thúc bằng lời gọi tool ⇒ còn lượt sau: cất phiên lại dưới khoá của hội thoại MỚI (kèm lượt
            // vừa trả lời). Mọi đường khác — xong việc, lỗi, bị hủy giữa chừng — đóng phiên.
            turn?.End(calledTools);
        }
    }

    /// <summary>
    /// Một lượt của vòng lặp agent chạy trên phiên CLI giữ sống.
    ///
    /// <para>
    /// Vì sao: mỗi lời gọi một phát là một phiên CLI mới nhận MỘT lượt user chở cả lịch sử, và prompt cache của
    /// Anthropic chỉ đọc lại được ở cuối một MESSAGE (đo thật qua CLI: cùng điểm cache, thêm một block phía
    /// sau trong cùng message là trượt). Lịch sử nhồi vào một message nên bước sau không bao giờ đọc lại được
    /// bước trước — ca JD Library 2026-11-26: bước Developer ghi cache 467k / đọc 31k token, hết hạn mức gói
    /// 5 giờ giữa bước 7. Giữ tiến trình sống thì hội thoại là các lượt THẬT trong CLI: mỗi bước đọc lại toàn bộ
    /// phần trước, chỉ ghi phần mới.
    /// </para>
    ///
    /// <para>
    /// Khớp phiên bằng dấu vân tay: phiên được cất dưới khoá = (đối số + system nền + bản ghi hội thoại TỚI HẾT
    /// lượt assistant vừa trả lời). Lượt sau tính khoá trên lịch sử tới lượt assistant cuối; trùng thì chỉ gửi
    /// phần đứng sau (kết quả tool, lời nhắc). Không trùng (app khởi động lại, phiên quá hạn, lịch sử bị đổi)
    /// ⇒ mở phiên mới và kể lại lịch sử như trước — chậm hơn, không bao giờ sai.
    /// </para>
    /// </summary>
    private sealed class SessionTurn
    {
        private readonly IClaudeCodeSessionPool _pool;
        private readonly string _identity;
        private readonly List<ChatMessage> _conversation;

        public IClaudeCodeSession Session { get; }
        public string StdinLine { get; }

        private SessionTurn(IClaudeCodeSessionPool pool, string identity, List<ChatMessage> conversation, IClaudeCodeSession session, string stdinLine)
        {
            _pool = pool;
            _identity = identity;
            _conversation = conversation;
            Session = session;
            StdinLine = stdinLine;
        }

        public static SessionTurn Begin(IClaudeCodeSessionPool pool, AiModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, ClaudeCodeInvocation invocation)
        {
            var identity = SessionIdentity(model, messages, options, invocation);
            var conversation = messages.Where(m => m.Role != ChatRole.System).ToList();

            var lastAssistant = conversation.FindLastIndex(m => m.Role == ChatRole.Assistant);
            if (lastAssistant >= 0 && lastAssistant < conversation.Count - 1
                && pool.Take(SessionKey(identity, conversation.Take(lastAssistant + 1))) is { } resumed)
                return new SessionTurn(pool, identity, conversation, resumed, ContinuationLine(conversation.Skip(lastAssistant + 1)));

            return new SessionTurn(pool, identity, conversation, pool.Open(invocation), invocation.StdinLine);
        }

        public void End(List<AIContent>? calledTools)
        {
            if (calledTools is null)
            {
                Session.Dispose();
                return;
            }
            var answered = _conversation.Append(new ChatMessage(ChatRole.Assistant, calledTools));
            _pool.Keep(SessionKey(_identity, answered), Session);
        }
    }

    /// <summary>
    /// Phần cố định của một phiên: model, đối số và system prompt NỀN (không gồm chỉ dẫn bản ghi — lượt đầu
    /// một phát thì không có, lượt sau có, nhưng cùng một phiên).
    /// </summary>
    private static string SessionIdentity(AiModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options, ClaudeCodeInvocation invocation) =>
        model.ModelId + "\u0001" + string.Join("\u0001", invocation.Arguments) + "\u0001"
        + string.Join("\n\n", BaseSystem(messages, options, AdvertisedFunctions(options)));

    /// <summary>Khoá kho phiên: băm của phần cố định + bản ghi hội thoại (kể cả ảnh, id lời gọi tool).</summary>
    internal static string SessionKey(string identity, IEnumerable<ChatMessage> conversation)
    {
        var text = identity + "\n" + TranscriptBlocks(conversation.ToList()).ToJsonString();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Dòng stdin của lượt nối tiếp: chỉ các message đứng sau lượt assistant cuối (kết quả tool, lời nhắc).</summary>
    private static string ContinuationLine(IEnumerable<ChatMessage> trailing)
    {
        var content = new JsonArray();
        foreach (var message in trailing)
            foreach (var block in ContentBlocks(message).ToList())
            {
                block.Parent?.AsArray().Remove(block);
                content.Add(block);
            }
        if (content.Count == 0)
            content.Add(TextBlock("Tiếp tục."));
        return UserLine(content);
    }

    private static string UserLine(JsonArray content) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
    }.ToJsonString();

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
        return Loc.T("ClaudeCodeChatClient.ErrorMessage.2", status, (string.IsNullOrWhiteSpace(result) ? Loc.T("ClaudeCodeChatClient.ErrorMessage") : result));
    }

    // ── Request ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lời gọi CLI tương đương lời gọi M.E.AI. Internal để test đọc được trực tiếp.</summary>
    internal static ClaudeCodeInvocation BuildInvocation(AiModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var functions = AdvertisedFunctions(options);
        var conversation = messages.Where(m => m.Role != ChatRole.System).ToList();
        // Một lượt user trần thì đưa nguyên văn; có lịch sử (hoặc có tool) thì kể lại thành bản ghi.
        var transcript = conversation.Count != 1 || conversation[0].Role != ChatRole.User;

        var system = BaseSystem(messages, options, functions);
        if (transcript)
            system.Insert(system.Count - (functions.Count > 0 ? 1 : 0), TranscriptInstruction);

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

        return new ClaudeCodeInvocation(arguments, string.Join("\n\n", system), UserLine(content));
    }

    // System prompt không phụ thuộc hình dạng hội thoại: chỉ dẫn của caller, message system, giao thức tool.
    // Phiên giữ sống dùng nó làm phần cố định (xem SessionIdentity) — chỉ dẫn bản ghi chen vào TRƯỚC giao thức tool.
    private static List<string> BaseSystem(IReadOnlyList<ChatMessage> messages, ChatOptions? options, IReadOnlyList<AIFunctionDeclaration> functions)
    {
        var system = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            system.Add(options.Instructions);
        system.AddRange(messages.Where(m => m.Role == ChatRole.System && !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text));
        if (functions.Count > 0)
            system.Add(ToolInstruction(functions, options!.ToolMode is RequiredChatToolMode));
        if (system.Count == 0)
            system.Add("Bạn là một trợ lý hữu ích.");
        return system;
    }

    // "Tin nhắn ĐẦU": một phiên giữ sống mở bằng bản ghi (lịch sử có sẵn mà không có phiên để nối) thì các
    // tin nhắn sau là lượt thường — xem SessionTurn.
    private const string TranscriptInstruction =
        "## Định dạng hội thoại\n"
        + "Tin nhắn ĐẦU của người dùng là BẢN GHI hội thoại tới lúc đó, mỗi lượt nằm trong một thẻ "
        + "<turn role=\"user|assistant|tool\">. Hãy viết ĐÚNG MỘT lượt assistant kế tiếp: chỉ nội dung của lượt đó, "
        + "không bọc trong thẻ <turn>, không nhắc lại bản ghi. Các tin nhắn sau (nếu có) là lượt kế tiếp bình thường.";

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
    ///
    /// <para>
    /// Ngoài khối <c>&lt;tool_call&gt;</c>, parser nhận cả dạng XML
    /// <c>&lt;invoke name="…"&gt;&lt;parameter name="…"&gt;…&lt;/parameter&gt;&lt;/invoke&gt;</c>. Giao thức chữ
    /// là ràng buộc mềm: khi tham số là hàng chục KB HTML/JS, model Claude hay bỏ JSON (phải escape từng dấu
    /// ngoặc kép) để viết dạng XML quen tay này. Không nhận thì cả lượt bị coi là lời văn — không tool nào
    /// chạy, vòng lặp agent kết thúc sau đúng một lời gọi model và trả về như thể đã xong việc.
    /// </para>
    /// </summary>
    internal static List<AIContent> ParseToolCalls(string text, IReadOnlyList<AIFunctionDeclaration>? functions = null)
    {
        var contents = new List<AIContent>();
        var position = NextCall(text, 0, out var invoke);
        var prose = InvokeWrapperPattern.Replace(position < 0 ? text : text[..position], string.Empty).Trim();
        if (prose.Length > 0)
            contents.Add(new TextContent(prose));

        while (position >= 0)
        {
            if (invoke is not null)
            {
                // Thân lời gọi kéo tới lời gọi kế tiếp (hoặc hết lượt): không dò </invoke> vì model có thể bị
                // cắt giữa chừng — tham số chưa đóng thẻ thì bỏ, rào chắn thiếu-tham-số của tool báo lại.
                var bodyStart = position + invoke.Length;
                var next = NextCall(text, bodyStart, out var nextInvoke);
                var body = InvokeTailPattern.Replace(next < 0 ? text[bodyStart..] : text[bodyStart..next], string.Empty);
                var name = invoke.Groups[1].Value;
                var schema = functions?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.JsonSchema;
                var arguments = new Dictionary<string, object?>();
                foreach (Match parameter in InvokeParameterPattern.Matches(body))
                    arguments[parameter.Groups[1].Value] = InvokeArgument(schema, parameter.Groups[1].Value, parameter.Groups[2].Value);
                contents.Add(new FunctionCallContent(NewCallId(), name, arguments));
                position = next;
                invoke = nextInvoke;
                continue;
            }

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

            var toolName = call?["name"] is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;
            if (call is not null && !string.IsNullOrWhiteSpace(toolName))
            {
                var arguments = call["arguments"] is JsonObject args
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.ToJsonString(), ArgumentJson)
                    : null;
                contents.Add(new FunctionCallContent(NewCallId(), toolName, arguments ?? new Dictionary<string, object?>()));
            }
            else
            {
                var match = ToolNamePattern.Match(text, start);
                contents.Add(new FunctionCallContent(NewCallId(), match.Success ? match.Groups[1].Value : InvalidToolCallName,
                    new Dictionary<string, object?>()));
            }

            // Đi tiếp từ sau JSON vừa đọc (tính theo ký tự, vì consumed là byte UTF-8).
            position = consumed == 0
                ? -1
                : NextCall(text, start + Encoding.UTF8.GetString(bytes, 0, consumed).Length, out invoke);
        }
        return contents;
    }

    /// <summary>Vị trí lời gọi tool kế tiếp từ <paramref name="from"/> (-1 nếu hết); <paramref name="invoke"/> khác null khi đó là dạng XML.</summary>
    private static int NextCall(string text, int from, out Match? invoke)
    {
        var json = text.IndexOf(ToolCallOpen, from, StringComparison.Ordinal);
        var xml = InvokeOpenPattern.Match(text, from);
        invoke = xml.Success && (json < 0 || xml.Index < json) ? xml : null;
        return invoke?.Index ?? json;
    }

    /// <summary>
    /// Tham số dạng XML luôn là chữ thô. Tham số mà schema của tool khai là chuỗi thì giữ nguyên văn (HTML/JS
    /// không bị đụng tới); loại khác (mảng, đối tượng, bool, số) thì đọc như JSON, không đọc được thì để chữ.
    /// </summary>
    private static object? InvokeArgument(JsonElement? schema, string name, string raw)
    {
        if (schema is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("type", out var type)
            && (type.ValueKind == JsonValueKind.String
                ? type.GetString() == "string"
                : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "string")))
            return raw;
        if (schema is null)
            return raw;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return raw; }
    }

    private static string NewCallId() => "call_" + Guid.NewGuid().ToString("N");
}
