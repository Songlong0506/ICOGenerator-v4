using System.ClientModel;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Claude trên Vertex AI (Bosch Model Farm Endpoint #2) nói Anthropic Messages chứ không nói OpenAI. Các test
/// này chạy client THẬT (qua factory + handler pipeline thật) vào một stub loopback đóng vai Vertex, rồi đọc
/// URL + body đi trên dây và kiểm cách dịch phản hồi về Microsoft.Extensions.AI.
/// </summary>
public class AnthropicVertexChatClientTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _root = null!;

    private readonly List<(string Path, JsonObject Body, string? Auth)> _requests = new();

    /// <summary>Phản hồi kế tiếp: (status, content-type, body). Null ⇒ trả lời mặc định theo stream/không stream.</summary>
    private readonly Queue<(int Status, string ContentType, string Body)> _replies = new();

    public Task InitializeAsync()
    {
        var port = FreePort();
        _root = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_root}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            lock (_requests)
                _requests.Add((context.Request.Url!.AbsolutePath, body, context.Request.Headers["Authorization"]));

            (int Status, string ContentType, string Body) reply;
            lock (_replies)
                reply = _replies.Count > 0 ? _replies.Dequeue()
                    : body["stream"]?.GetValue<bool>() == true ? (200, "text/event-stream", TextStream("{\"answer\":\"ok\"}"))
                    : (200, "application/json", TextMessage("pong"));

            var bytes = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.ContentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static string TextMessage(string text) =>
        new JsonObject
        {
            ["id"] = "msg_1",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-haiku-5-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = "end_turn",
            ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 2 },
        }.ToJsonString();

    private static string Sse(params JsonObject[] events) =>
        string.Concat(events.Select(e => $"event: {e["type"]}\ndata: {e.ToJsonString()}\n\n"));

    private static string TextStream(string text) => Sse(
        new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = "msg_2", ["model"] = "claude-haiku-5-5",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 10, ["cache_read_input_tokens"] = 1000, ["cache_creation_input_tokens"] = 5,
                    ["output_tokens"] = 1,
                },
            },
        },
        new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } },
        new JsonObject { ["type"] = "ping" },
        new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text[..3] } },
        new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text[3..] } },
        new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 },
        new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" }, ["usage"] = new JsonObject { ["output_tokens"] = 20 } },
        new JsonObject { ["type"] = "message_stop" });

    private (string Path, JsonObject Body, string? Auth) Request(int index)
    {
        lock (_requests)
            return _requests[index];
    }

    private static IChatClientFactory Factory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName)
            .AddHttpMessageHandler(() => new LlmRequestCompatibilityHandler());
        return new OpenAIChatClientFactory(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    // Đúng dạng người dùng dán từ tài liệu BMF: có cả /models/… và method :generateContent của Gemini.
    private AiModel Model(StructuredOutputMode mode = StructuredOutputMode.None) => new()
    {
        ModelId = "claude-haiku-5-5",
        Endpoint = $"{_root}/api/google/v1/publishers/anthropic/models/claude-haiku-5-5:generateContent",
        ApiKey = "bmf-key",
        StructuredOutputMode = mode,
    };

    [Theory]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic", true)]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic/models/claude-haiku-5-5:generateContent", true)]
    [InlineData("https://us-east5-aiplatform.googleapis.com/v1/projects/p/locations/us-east5/publishers/anthropic/models", true)]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/publishers/google/models/gemini-3.7-flash:generateContent", false)]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/endpoints/gemini-3.7-flash/openapi", false)]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22?api-version=2025-04-01-preview", false)]
    [InlineData("https://example.com/publishers/anthropicx", false)]
    [InlineData("not a url", false)]
    public void RecognisesClaudeOnVertexByPath(string endpoint, bool expected) =>
        Assert.Equal(expected, AnthropicVertexChatClient.IsAnthropicVertexEndpoint(endpoint));

    [Fact]
    public void Factory_RoutesAnthropicEndpointToAnthropicClient()
    {
        Assert.IsType<AnthropicVertexChatClient>(Factory().Create(Model()));
    }

    [Fact]
    public void RequestUri_RebuildsModelAndMethod_FromModelId()
    {
        var model = Model();
        model.ModelId = "claude-opus-4-5@20251101";

        Assert.Equal("/api/google/v1/publishers/anthropic/models/claude-opus-4-5@20251101:streamRawPredict",
            AnthropicVertexChatClient.RequestUri(model, stream: true).AbsolutePath);
        Assert.Equal("/api/google/v1/publishers/anthropic/models/claude-opus-4-5@20251101:rawPredict",
            AnthropicVertexChatClient.RequestUri(model, stream: false).AbsolutePath);
    }

    // Đường của nút Test Connection: GetResponseAsync ⇒ :rawPredict, khóa dạng Bearer, body Anthropic.
    [Fact]
    public async Task ConnectionTest_PostsRawPredict_AndReadsTheAnswer()
    {
        var tester = new ModelConnectionTester(Factory(), new LlmSettings());

        var outcome = await tester.TestAsync(Model());

        Assert.True(outcome.IsSuccess, outcome.ErrorMessage);
        Assert.Equal("pong", outcome.Detail);
        var (path, body, auth) = Request(0);
        Assert.EndsWith("/publishers/anthropic/models/claude-haiku-5-5:rawPredict", path);
        Assert.Equal("Bearer bmf-key", auth);
        Assert.Equal(AnthropicVertexChatClient.AnthropicVersion, body["anthropic_version"]!.GetValue<string>());
        Assert.Equal(16, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("stream"));
        Assert.False(body.ContainsKey("model"));
    }

    [Fact]
    public async Task ApiError_SurfacesStatusLikeTheOpenAiPath()
    {
        lock (_replies)
            _replies.Enqueue((404, "application/json", """{"error":{"type":"not_found_error","message":"model not found"}}"""));

        var outcome = await new ModelConnectionTester(Factory(), new LlmSettings()).TestAsync(Model());

        Assert.False(outcome.IsSuccess);
        Assert.Equal(404, outcome.HttpStatusCode);
        Assert.Contains("model not found", outcome.Detail);
    }

    // Đường BA: stream qua :streamRawPredict, system tách riêng, JSON mode thành chỉ dẫn (không response_format),
    // token cache đọc ra đúng chỗ để tính tiền.
    [Fact]
    public async Task StructuredChat_StreamsAndTranslatesUsage()
    {
        var client = new LlmClient(Factory(), new ICOGenerator.Services.Evals.NullModelCallLogger(), new NoBudgetLimit(), new LlmSettings(),
            NullLogger<LlmClient>.Instance);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Bạn là BA. Trả về json."),
            new(ChatRole.Assistant, "Chào bạn!"),
            new(ChatRole.User, "trả lời bằng json"),
        };
        var tokens = new StringBuilder();

        var (result, value) = await client.ChatStructuredAsync<Reply>(
            Model(StructuredOutputMode.JsonSchema), messages, new ModelCallLogContext(Guid.NewGuid(), new Agent(), "AnthropicTest"),
            onToken: t => tokens.Append(t));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("ok", value?.Answer);
        Assert.Equal("{\"answer\":\"ok\"}", tokens.ToString());
        Assert.Equal(1015, result.PromptTokens);
        Assert.Equal(1000, result.CachedPromptTokens);
        Assert.Equal(20, result.CompletionTokens);

        var (path, body, _) = Request(0);
        Assert.EndsWith(":streamRawPredict", path);
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("response_format"));

        var system = body["system"]!.AsArray();
        Assert.Equal("Bạn là BA. Trả về json.", system[0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Contains(AnthropicVertexChatClient.ResponseToolName, system[1]!["text"]!.GetValue<string>());

        // JsonSchema ⇒ ÉP gọi tool câu trả lời mang đúng schema (thay cho response_format).
        Assert.Equal(AnthropicVertexChatClient.ResponseToolName, body["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", body["tool_choice"]!["type"]!.GetValue<string>());
        var tool = Assert.Single(body["tools"]!.AsArray())!;
        Assert.Equal(AnthropicVertexChatClient.ResponseToolName, tool["name"]!.GetValue<string>());
        Assert.NotNull(tool["input_schema"]!["properties"]!["answer"]);
        Assert.Null(tool["input_schema"]!["$schema"]);

        // Lịch sử mở bằng assistant ⇒ chèn một lượt user mở đầu; mọi role đều là user/assistant xen kẽ.
        var turns = body["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, turns.Select(t => t!["role"]!.GetValue<string>()));
        Assert.Equal("ephemeral", turns[2]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>());
    }

    // Phản hồi thật khi bị ép tool: input_json_delta của tool câu trả lời phát thành TEXT (stream "BA đang gõ"
    // vẫn chạy), stop_reason tool_use đọc là Stop, và LlmClient đọc ra T như khi model tự viết JSON.
    [Fact]
    public async Task ForcedResponseTool_StreamsInputAsText()
    {
        lock (_replies)
            _replies.Enqueue((200, "text/event-stream", Sse(
                new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "m", ["usage"] = new JsonObject { ["input_tokens"] = 3 } } },
                new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = AnthropicVertexChatClient.ResponseToolName, ["input"] = new JsonObject() } },
                new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "" } },
                new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"answer\":" } },
                new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "\"ok\"}" } },
                new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 },
                new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" }, ["usage"] = new JsonObject { ["output_tokens"] = 9 } },
                new JsonObject { ["type"] = "message_stop" })));

        var client = new LlmClient(Factory(), new ICOGenerator.Services.Evals.NullModelCallLogger(), new NoBudgetLimit(), new LlmSettings(),
            NullLogger<LlmClient>.Instance);
        var tokens = new StringBuilder();

        var (result, value) = await client.ChatStructuredAsync<Reply>(
            Model(StructuredOutputMode.JsonSchema), [new ChatMessage(ChatRole.User, "trả lời bằng json")],
            new ModelCallLogContext(Guid.NewGuid(), new Agent(), "AnthropicTest"), onToken: t => tokens.Append(t));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("ok", value?.Answer);
        Assert.Equal("{\"answer\":\"ok\"}", result.Content);
        Assert.Equal("{\"answer\":\"ok\"}", tokens.ToString());
        // Không có lời gọi thừa: phản hồi đã là JSON nên lưới "đòi lại" không chạy.
        lock (_requests)
            Assert.Single(_requests);
    }

    [Fact]
    public void ForcedResponseTool_NonStreamingReplyBecomesText()
    {
        var response = AnthropicVertexChatClient.ParseResponse(new JsonObject
        {
            ["id"] = "m",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = AnthropicVertexChatClient.ResponseToolName,
                ["input"] = new JsonObject { ["answer"] = "ok" },
            }),
            ["stop_reason"] = "tool_use",
        });

        Assert.Equal("{\"answer\":\"ok\"}", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    // JsonObject (không schema) vẫn ép được — schema object trống, đúng ngữ nghĩa json_object.
    [Fact]
    public void JsonObjectMode_ForcesAGenericObjectTool()
    {
        var body = AnthropicVertexChatClient.BuildRequest(
            [new ChatMessage(ChatRole.User, "json")], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, stream: true);

        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal(AnthropicVertexChatClient.ResponseToolName, body["tool_choice"]!["name"]!.GetValue<string>());
    }

    // Lượt có tool THẬT không ép được tool câu trả lời (model sẽ hết đường gọi tool của nó) ⇒ lùi về chỉ dẫn
    // kèm schema trong system.
    [Fact]
    public void RealTools_FallBackToSystemInstruction()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(ReadFile, "read_file")],
            ResponseFormat = ChatResponseFormat.ForJsonSchema<Reply>(),
        };
        var body = AnthropicVertexChatClient.BuildRequest([new ChatMessage(ChatRole.User, "đọc")], options, stream: true);

        Assert.Equal("read_file", Assert.Single(body["tools"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.Contains("\"answer\"", body["system"]!.AsArray().Last()!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamedToolUse_BecomesFunctionCall()
    {
        lock (_replies)
            _replies.Enqueue((200, "text/event-stream", Sse(
                new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "m", ["usage"] = new JsonObject { ["input_tokens"] = 3 } } },
                new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "read_file", ["input"] = new JsonObject() } },
                new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":" } },
                new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "\"a.md\"}" } },
                new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 },
                new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" }, ["usage"] = new JsonObject { ["output_tokens"] = 9 } },
                new JsonObject { ["type"] = "message_stop" })));

        var client = Factory().Create(Model());
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(ReadFile, "read_file")], MaxOutputTokens = 100 };
        var response = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "đọc a.md")], options).ToChatResponseAsync();

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("toolu_1", call.CallId);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.md", call.Arguments!["path"]!.ToString());

        var tool = Request(0).Body["tools"]![0]!;
        Assert.Equal("read_file", tool["name"]!.GetValue<string>());
        Assert.Equal("object", tool["input_schema"]!["type"]!.GetValue<string>());
    }

    // Lượt tiếp theo của vòng lặp tool: tool_use của assistant + tool_result trong lượt user ngay sau.
    [Fact]
    public void ToolRoundTrip_MapsToToolUseAndToolResult()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "đọc a.md"),
            new(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "read_file", new Dictionary<string, object?> { ["path"] = "a.md" })]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "nội dung")]),
        };

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(ReadFile, "read_file")] };
        var body = AnthropicVertexChatClient.BuildRequest(messages, options, stream: true);

        var turns = body["messages"]!.AsArray();
        Assert.Equal(3, turns.Count);
        var toolUse = turns[1]!["content"]![0]!;
        Assert.Equal("tool_use", toolUse["type"]!.GetValue<string>());
        Assert.Equal("a.md", toolUse["input"]!["path"]!.GetValue<string>());
        var toolResult = turns[2]!["content"]![0]!;
        Assert.Equal("user", turns[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.Equal("nội dung", toolResult["content"]!.GetValue<string>());
    }

    // Lượt "chốt kết quả" của agent: Tools = [] trên lịch sử có tool ⇒ kể lại bằng chữ (Anthropic 400 nếu còn
    // block tool_use/tool_result mà không khai tools). Instructions của agent đi vào system.
    [Fact]
    public void ToolHistoryWithoutTools_IsNarratedAsText_AndInstructionsBecomeSystem()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "đọc a.md"),
            new(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "read_file", new Dictionary<string, object?> { ["path"] = "a.md" })]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "nội dung")]),
            new(ChatRole.User, "tóm tắt"),
        };

        var body = AnthropicVertexChatClient.BuildRequest(messages, new ChatOptions { Instructions = "Bạn là Dev.", Tools = [] }, stream: true);

        Assert.Equal("Bạn là Dev.", body["system"]![0]!["text"]!.GetValue<string>());
        Assert.False(body.ContainsKey("tools"));
        var types = body["messages"]!.AsArray().SelectMany(t => t!["content"]!.AsArray()).Select(b => b!["type"]!.GetValue<string>());
        Assert.All(types, t => Assert.Equal("text", t));
    }

    [Fact]
    public void Images_GoAsBase64ImageBlocks()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("xem hình"), new DataContent(png, "image/png")]),
        };

        var body = AnthropicVertexChatClient.BuildRequest(messages, new ChatOptions(), stream: true);

        var image = body["messages"]![0]!["content"]![1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(png), image["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void CallLogPreview_ShowsAnthropicShape_NotResponseFormat()
    {
        var preview = JsonNode.Parse(ModelCallRequestPreview.Build(
            Model(), [new ChatMessage(ChatRole.User, "json")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, 100, streaming: true))!;

        Assert.Equal(AnthropicVertexChatClient.AnthropicVersion, preview["anthropic_version"]!.GetValue<string>());
        Assert.Null(preview["response_format"]);
        Assert.NotNull(preview["_structured_output_instruction"]);
        Assert.Equal(AnthropicVertexChatClient.ResponseToolName, preview["tool_choice"]!["name"]!.GetValue<string>());
    }

    [Description("Đọc file")]
    private static string ReadFile([Description("đường dẫn")] string path) => path;

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Reply
    {
        public string? Answer { get; set; }
    }
}
