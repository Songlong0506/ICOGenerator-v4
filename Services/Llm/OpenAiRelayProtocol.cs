using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>Một request <c>/chat/completions</c> đã đọc xong — xem <see cref="OpenAiRelayProtocol.TryParseRequest"/>.</summary>
public sealed record RelayChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions Options, bool Stream);

/// <summary>
/// Giao thức OpenAI Chat Completions ở phía SERVER, đủ cho một agent CLI bên ngoài (opencode) gọi model
/// qua app thay vì gọi thẳng endpoint: đọc request (tin nhắn, tool, tham số) thành
/// <see cref="ChatMessage"/>/<see cref="ChatOptions"/> của Microsoft.Extensions.AI, và viết ngược
/// <see cref="ChatResponseUpdate"/> thành các chunk <c>chat.completion.chunk</c> (SSE) hoặc một
/// <c>chat.completion</c> trọn.
///
/// <para>
/// Vì sao DỊCH chứ không chuyển tiếp nguyên byte: lời gọi phải đi qua đúng đường ống mọi lời gọi khác
/// của app đi qua — <see cref="IChatClientFactory"/> (proxy công ty, Claude trên Vertex vốn không có bản
/// tương thích OpenAI, các vá request của <see cref="LlmRequestCompatibilityHandler"/>) và
/// <see cref="ModelCallLoggingChatClient"/> (budget guard, deadline, trần output, call log). Chuyển tiếp
/// nguyên byte thì phải chép lại từng thứ đó, và lời gọi của agent bên ngoài sẽ vô hình với trang Usage.
/// </para>
///
/// <para>
/// Phạm vi CỐ Ý hẹp: chỉ những gì một agent lập trình gửi — text, ảnh dạng <c>image_url</c>, tool
/// (function), kết quả tool, <c>max_tokens</c>/<c>temperature</c>/<c>tool_choice</c>. Không có
/// <c>n &gt; 1</c>, logprobs, audio.
/// </para>
/// </summary>
public static class OpenAiRelayProtocol
{
    /// <summary>
    /// Đọc body request. Trả <c>null</c> kèm lý do khi thiếu <c>messages</c> hoặc một tin nhắn sai hình —
    /// relay trả 400 với đúng lý do đó, để lỗi của agent bên ngoài lộ ra ở log của chính nó.
    /// </summary>
    public static RelayChatRequest? TryParseRequest(JsonElement body, out string error)
    {
        error = string.Empty;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("messages", out var messagesElement)
            || messagesElement.ValueKind != JsonValueKind.Array)
        {
            error = "request thiếu mảng 'messages'";
            return null;
        }

        var messages = new List<ChatMessage>();
        foreach (var item in messagesElement.EnumerateArray())
        {
            var message = ParseMessage(item, out error);
            if (message == null)
                return null;
            messages.Add(message);
        }

        if (messages.Count == 0)
        {
            error = "mảng 'messages' rỗng";
            return null;
        }

        var options = new ChatOptions();
        if (TryGetInt(body, "max_completion_tokens", out var maxTokens) || TryGetInt(body, "max_tokens", out maxTokens))
            options.MaxOutputTokens = maxTokens;
        if (body.TryGetProperty("temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Number)
            options.Temperature = temperature.GetSingle();
        if (body.TryGetProperty("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number)
            options.TopP = topP.GetSingle();

        if (body.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var declarations = new List<AITool>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (!tool.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                    continue;
                var name = GetString(function, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var schema = function.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                    ? parameters.Clone()
                    : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
                declarations.Add(new RelayToolDeclaration(name, GetString(function, "description") ?? string.Empty, schema));
            }

            if (declarations.Count > 0)
            {
                options.Tools = declarations;
                options.ToolMode = ParseToolMode(body);
            }
        }

        var stream = body.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True;
        return new RelayChatRequest(messages, options, stream);
    }

    private static ChatMessage? ParseMessage(JsonElement item, out string error)
    {
        error = string.Empty;
        var role = GetString(item, "role");
        switch (role)
        {
            case "system":
            case "developer":
                return new ChatMessage(ChatRole.System, ContentText(item));

            case "user":
                return new ChatMessage(ChatRole.User, UserContents(item));

            case "assistant":
            {
                var contents = new List<AIContent>();
                var text = ContentText(item);
                if (text.Length > 0)
                    contents.Add(new TextContent(text));
                if (item.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var function))
                            continue;
                        contents.Add(new FunctionCallContent(
                            GetString(call, "id") ?? string.Empty,
                            GetString(function, "name") ?? string.Empty,
                            ParseArguments(GetString(function, "arguments"))));
                    }
                }
                return new ChatMessage(ChatRole.Assistant, contents);
            }

            case "tool":
            {
                var callId = GetString(item, "tool_call_id");
                if (string.IsNullOrEmpty(callId))
                {
                    error = "tin nhắn role 'tool' thiếu 'tool_call_id'";
                    return null;
                }
                return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, ContentText(item))]);
            }

            default:
                error = $"role tin nhắn không hỗ trợ: '{role}'";
                return null;
        }
    }

    // "content" là chuỗi, hoặc mảng phần { type: "text", text } — gộp các phần chữ.
    private static string ContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (GetString(part, "type") == "text" && GetString(part, "text") is { } text)
                sb.Append(text);
        }
        return sb.ToString();
    }

    private static List<AIContent> UserContents(JsonElement item)
    {
        var contents = new List<AIContent>();
        if (!item.TryGetProperty("content", out var content))
            return contents;
        if (content.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextContent(content.GetString() ?? string.Empty));
            return contents;
        }
        if (content.ValueKind != JsonValueKind.Array)
            return contents;

        foreach (var part in content.EnumerateArray())
        {
            switch (GetString(part, "type"))
            {
                case "text":
                    contents.Add(new TextContent(GetString(part, "text") ?? string.Empty));
                    break;
                case "image_url":
                    var url = part.TryGetProperty("image_url", out var image) ? GetString(image, "url") : null;
                    if (url == null)
                        break;
                    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        contents.Add(new DataContent(url));
                    else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        contents.Add(new UriContent(uri, "image/*"));
                    break;
            }
        }
        return contents;
    }

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException)
        {
            // Model viết arguments hỏng ở lượt trước: giữ nguyên văn để lượt sau thấy chính chuỗi đó.
            return new Dictionary<string, object?> { ["_raw"] = json };
        }
    }

    private static ChatToolMode ParseToolMode(JsonElement body)
    {
        if (!body.TryGetProperty("tool_choice", out var choice))
            return ChatToolMode.Auto;
        if (choice.ValueKind == JsonValueKind.String)
        {
            return choice.GetString() switch
            {
                "none" => ChatToolMode.None,
                "required" => ChatToolMode.RequireAny,
                _ => ChatToolMode.Auto
            };
        }
        return choice.ValueKind == JsonValueKind.Object
               && choice.TryGetProperty("function", out var function)
               && GetString(function, "name") is { Length: > 0 } name
            ? ChatToolMode.RequireSpecific(name)
            : ChatToolMode.Auto;
    }

    // ---- phía trả lời ----

    /// <summary>
    /// Chunk SSE (phần sau <c>data: </c>) cho MỘT update của model; <c>null</c> khi update không mang gì
    /// agent bên ngoài cần (vd chỉ có usage — usage đi riêng ở <see cref="UsageChunk"/>).
    /// <paramref name="toolIndex"/> đếm số tool call đã phát trong câu trả lời: OpenAI đánh <c>index</c>
    /// theo thứ tự call, client ghép các mảnh theo index đó.
    /// </summary>
    public static string? UpdateChunk(string id, string model, long created, ChatResponseUpdate update, ref int toolIndex, ref bool roleSent)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<FunctionCallContent>();
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent t:
                    text.Append(t.Text);
                    break;
                case TextReasoningContent r:
                    reasoning.Append(r.Text);
                    break;
                case FunctionCallContent f:
                    calls.Add(f);
                    break;
            }
        }

        if (text.Length == 0 && reasoning.Length == 0 && calls.Count == 0)
            return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteChunkHead(w, id, model, created);
            w.WriteStartArray("choices");
            w.WriteStartObject();
            w.WriteNumber("index", 0);
            w.WriteStartObject("delta");
            if (!roleSent)
            {
                w.WriteString("role", "assistant");
                roleSent = true;
            }
            if (text.Length > 0)
                w.WriteString("content", text.ToString());
            if (reasoning.Length > 0)
                w.WriteString("reasoning_content", reasoning.ToString());
            if (calls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var call in calls)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", toolIndex++);
                    WriteToolCallBody(w, call);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteNull("finish_reason");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Chunk chốt: delta rỗng + <c>finish_reason</c>.</summary>
    public static string FinishChunk(string id, string model, long created, string finishReason)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteChunkHead(w, id, model, created);
            w.WriteStartArray("choices");
            w.WriteStartObject();
            w.WriteNumber("index", 0);
            w.WriteStartObject("delta");
            w.WriteEndObject();
            w.WriteString("finish_reason", finishReason);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Chunk usage (<c>choices</c> rỗng) — như OpenAI gửi khi client xin <c>include_usage</c>.</summary>
    public static string UsageChunk(string id, string model, long created, UsageDetails usage)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteChunkHead(w, id, model, created);
            w.WriteStartArray("choices");
            w.WriteEndArray();
            WriteUsage(w, usage);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Câu trả lời trọn (<c>stream: false</c>).</summary>
    public static string Completion(string id, string model, long created, ChatResponse response)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<FunctionCallContent>();
        foreach (var content in response.Messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case TextContent t: text.Append(t.Text); break;
                case TextReasoningContent r: reasoning.Append(r.Text); break;
                case FunctionCallContent f: calls.Add(f); break;
            }
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("object", "chat.completion");
            w.WriteNumber("created", created);
            w.WriteString("model", model);
            w.WriteStartArray("choices");
            w.WriteStartObject();
            w.WriteNumber("index", 0);
            w.WriteStartObject("message");
            w.WriteString("role", "assistant");
            if (text.Length > 0) w.WriteString("content", text.ToString());
            else w.WriteNull("content");
            if (reasoning.Length > 0)
                w.WriteString("reasoning_content", reasoning.ToString());
            if (calls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var call in calls)
                {
                    w.WriteStartObject();
                    WriteToolCallBody(w, call);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteString("finish_reason", FinishReason(response.FinishReason, calls.Count > 0));
            w.WriteEndObject();
            w.WriteEndArray();
            if (response.Usage != null)
                WriteUsage(w, response.Usage);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Thân lỗi theo đúng hình OpenAI — client (AI SDK) đọc <c>error.message</c> để báo lên người dùng.</summary>
    public static string Error(string message, string type)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("message", message);
            w.WriteString("type", type);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string FinishReason(ChatFinishReason? reason, bool hasToolCalls)
    {
        if (hasToolCalls || reason == ChatFinishReason.ToolCalls)
            return "tool_calls";
        if (reason == ChatFinishReason.Length)
            return "length";
        if (reason == ChatFinishReason.ContentFilter)
            return "content_filter";
        return "stop";
    }

    private static void WriteChunkHead(Utf8JsonWriter w, string id, string model, long created)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("object", "chat.completion.chunk");
        w.WriteNumber("created", created);
        w.WriteString("model", model);
    }

    private static void WriteToolCallBody(Utf8JsonWriter w, FunctionCallContent call)
    {
        w.WriteString("id", string.IsNullOrEmpty(call.CallId) ? "call_" + Guid.NewGuid().ToString("N")[..24] : call.CallId);
        w.WriteString("type", "function");
        w.WriteStartObject("function");
        w.WriteString("name", call.Name);
        w.WriteString("arguments", JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()));
        w.WriteEndObject();
    }

    private static void WriteUsage(Utf8JsonWriter w, UsageDetails usage)
    {
        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        w.WriteStartObject("usage");
        w.WriteNumber("prompt_tokens", input);
        w.WriteNumber("completion_tokens", output);
        w.WriteNumber("total_tokens", usage.TotalTokenCount ?? input + output);
        w.WriteEndObject();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value) && value > 0;
    }

    /// <summary>
    /// Tool do agent bên ngoài khai báo: chỉ là MÔ TẢ gửi cho model, không có thân hàm — relay trả lời
    /// <see cref="FunctionCallContent"/> về cho chính agent đó thực thi.
    /// </summary>
    private sealed class RelayToolDeclaration(string name, string description, JsonElement schema) : AIFunctionDeclaration
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;
    }
}
