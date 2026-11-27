using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Model chạy qua Claude Code CLI (Endpoint <c>claude-code://…</c>). Test thay tiến trình <c>claude</c> bằng
/// một runner giả phát lại đúng các dòng stream-json CLI thật in ra, rồi kiểm lời gọi dựng ra (đối số, system
/// prompt, stdin) và cách dịch phản hồi về Microsoft.Extensions.AI.
/// </summary>
public class ClaudeCodeChatClientTests
{
    private static readonly AiModel Model = new() { ModelId = "claude-sonnet-5-5", Endpoint = "claude-code://subscription" };

    private sealed class FakeRunner(params string[] lines) : IClaudeCodeRunner
    {
        public ClaudeCodeInvocation? Invocation { get; private set; }

        public async IAsyncEnumerable<string> RunAsync(
            ClaudeCodeInvocation invocation, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Invocation = invocation;
            foreach (var line in lines)
            {
                await Task.Yield();
                yield return line;
            }
        }
    }

    private static string Event(string json) => $$"""{"type":"stream_event","event":{{json}},"session_id":"s"}""";

    private static string[] TextTurn(params string[] deltas) =>
    [
        """{"type":"system","subtype":"init","tools":[]}""",
        Event("""{"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-5-5","usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":30,"output_tokens":1}}}"""),
        Event("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        .. deltas.Select(d => Event(new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = 0,
            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = d },
        }.ToJsonString())),
        Event("""{"type":"content_block_stop","index":0}"""),
        Event("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}"""),
        """{"type":"result","subtype":"success","is_error":false,"result":"ignored"}""",
    ];

    [Description("Đọc một file")]
    private static string ReadFile([Description("Đường dẫn")] string path) => path;

    private static ChatOptions WithTool() => new() { Tools = [AIFunctionFactory.Create(ReadFile, "read_file")] };

    [Fact]
    public void Factory_routes_claude_code_endpoint_to_the_cli_client()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var factory = new OpenAIChatClientFactory(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());

        Assert.IsType<ClaudeCodeChatClient>(factory.Create(Model));
        Assert.True(ClaudeCodeCli.IsClaudeCodeEndpoint("Claude-Code://subscription"));
        Assert.False(ClaudeCodeCli.IsClaudeCodeEndpoint("https://api.anthropic.com/v1"));
    }

    [Fact]
    public void Invocation_uses_the_cli_only_as_a_model_transport()
    {
        var invocation = ClaudeCodeChatClient.BuildInvocation(Model,
            [new ChatMessage(ChatRole.System, "Bạn là BA."), new ChatMessage(ChatRole.User, "Xin chào")],
            new ChatOptions { Instructions = "Prompt nền." });

        var args = invocation.Arguments.ToList();
        Assert.Equal("claude-sonnet-5-5", args[args.IndexOf("--model") + 1]);
        // Tắt hết tool có sẵn của Claude Code và mọi nguồn settings: chỉ còn đường gọi model.
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--json-schema", args);

        Assert.Equal("Prompt nền.\n\nBạn là BA.", invocation.SystemPrompt);
        var content = JsonNode.Parse(invocation.StdinLine)!["message"]!["content"]!.AsArray();
        Assert.Equal("Xin chào", content.Single()!["text"]!.GetValue<string>());
    }

    [Fact]
    public void History_is_replayed_as_a_transcript_with_tool_calls_and_images()
    {
        var invocation = ClaudeCodeChatClient.BuildInvocation(Model,
        [
            new ChatMessage(ChatRole.User, [new TextContent("Xem ảnh"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a.md" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "nội dung")]),
        ], WithTool());

        var content = JsonNode.Parse(invocation.StdinLine)!["message"]!["content"]!.AsArray();
        Assert.Equal(["text", "image", "text"], content.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("<turn role=\"user\">\nXem ảnh\n", content[0]!["text"]!.GetValue<string>());
        var rest = content[2]!["text"]!.GetValue<string>();
        Assert.Contains("<turn role=\"assistant\">\n<tool_call>{\"id\":\"c1\",\"name\":\"read_file\",\"arguments\":{\"path\":\"a.md\"}}</tool_call>", rest);
        Assert.Contains("<turn role=\"tool\">\n<tool_result id=\"c1\">\nnội dung\n</tool_result>", rest);

        Assert.Contains("## Định dạng hội thoại", invocation.SystemPrompt);
        Assert.Contains("## Gọi tool", invocation.SystemPrompt);
        Assert.Contains("\"name\":\"read_file\"", invocation.SystemPrompt);
    }

    [Fact]
    public void Structured_output_goes_through_json_schema_unless_real_tools_are_advertised()
    {
        var json = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var args = ClaudeCodeChatClient.BuildInvocation(Model, [new ChatMessage(ChatRole.User, "x")], json).Arguments.ToList();
        Assert.Equal("{\"type\":\"object\"}", args[args.IndexOf("--json-schema") + 1]);

        var schema = new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<Answer>() };
        args = ClaudeCodeChatClient.BuildInvocation(Model, [new ChatMessage(ChatRole.User, "x")], schema).Arguments.ToList();
        Assert.Contains("\"city\"", args[args.IndexOf("--json-schema") + 1]);

        var withTools = WithTool();
        withTools.ResponseFormat = ChatResponseFormat.Json;
        Assert.DoesNotContain("--json-schema",
            ClaudeCodeChatClient.BuildInvocation(Model, [new ChatMessage(ChatRole.User, "x")], withTools).Arguments);
    }

    private sealed record Answer(string City);

    // Không đặt thì CLI tự đi effort "high" + adaptive thinking (ca JD Library: ~3/4 token output của bước
    // Developer là suy luận). Mức của model đi ra --effort; CLI không tắt được suy luận nên None đi ra low.
    [Theory]
    [InlineData(null, null)]
    [InlineData(ReasoningEffort.None, "low")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    public void Reasoning_effort_goes_out_as_the_cli_effort_flag(ReasoningEffort? effort, string? expected)
    {
        var options = new ChatOptions { Reasoning = effort is { } e ? new ReasoningOptions { Effort = e } : null };

        var args = ClaudeCodeChatClient.BuildInvocation(Model, [new ChatMessage(ChatRole.User, "x")], options).Arguments.ToList();

        Assert.Equal(expected, args.Contains("--effort") ? args[args.IndexOf("--effort") + 1] : null);
    }

    [Fact]
    public async Task Text_turn_streams_deltas_and_reports_usage()
    {
        var client = new ClaudeCodeChatClient(Model, new FakeRunner(TextTurn("Xin ", "chào")));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(update);

        Assert.Equal(["Xin ", "chào"], updates.Select(u => u.Text).Where(t => t.Length > 0));
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
        var usage = updates[^1].Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(42, usage.InputTokenCount);
        Assert.Equal(30, usage.CachedInputTokenCount);
        Assert.Equal(7, usage.OutputTokenCount);
    }

    [Fact]
    public async Task Tool_turn_turns_tool_call_blocks_into_function_calls_and_hides_the_json()
    {
        var client = new ClaudeCodeChatClient(Model, new FakeRunner(TextTurn(
            "Tôi sẽ đọc file.\n<tool_", "call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"a </tool_call> b.md\"}}</tool_call>\n",
            "<tool_call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"c.md\"}}")));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "đọc")], WithTool());

        Assert.Equal("Tôi sẽ đọc file.", response.Text);
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["read_file", "read_file"], calls.Select(c => c.Name));
        // Thẻ đóng nằm TRONG chuỗi JSON không cắt cụt tham số; khối thứ hai thiếu thẻ đóng vẫn đọc được.
        Assert.Equal("a </tool_call> b.md", calls[0].Arguments!["path"]?.ToString());
        Assert.Equal("c.md", calls[1].Arguments!["path"]?.ToString());
        Assert.NotEqual(calls[0].CallId, calls[1].CallId);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    // Tham số là hàng chục KB HTML thì model hay bỏ khối JSON để viết dạng XML <invoke>. Trước đây cả lượt bị
    // coi là lời văn: không tool nào chạy, agent Developer "xong" sau đúng một lời gọi model với POC còn trống.
    [Fact]
    public void Xml_invoke_blocks_become_function_calls_with_arguments_typed_by_the_tool_schema()
    {
        var tool = AIFunctionFactory.Create(
            (string content, string? appName, JsonElement? navItems, bool audit) => "ok", "SetPocContent");
        const string text =
            "Dựng nhóm màn đầu.\n<function_calls>\n"
            + "<invoke name=\"SetPocContent\">\n<parameter name=\"content\"><section class=\"page-view\">\n{\"a\":1}</section></parameter>\n"
            + "<parameter name=\"appName\">123</parameter>\n"
            + "<parameter name=\"navItems\">[{\"label\":\"Reports\"}]</parameter>\n"
            + "<parameter name=\"audit\">true</parameter>\n</invoke>\n"
            + "<invoke name=\"AppendPocContent\">\n<parameter name=\"content\"><p>x</p></parameter>\n</invoke>\n</function_calls>";

        var contents = ClaudeCodeChatClient.ParseToolCalls(text, [tool]);

        Assert.Equal("Dựng nhóm màn đầu.", Assert.IsType<TextContent>(contents[0]).Text);
        var calls = contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(["SetPocContent", "AppendPocContent"], calls.Select(c => c.Name));
        // Tham số chuỗi giữ nguyên văn, kể cả khi trông giống JSON/số; mảng và bool đọc như JSON.
        Assert.Equal("<section class=\"page-view\">\n{\"a\":1}</section>", calls[0].Arguments!["content"]);
        Assert.Equal("123", calls[0].Arguments!["appName"]);
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(calls[0].Arguments!["navItems"]).ValueKind);
        Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(calls[0].Arguments!["audit"]).ValueKind);
        // Tool không có trong danh sách: không có schema để đoán kiểu, giữ chữ.
        Assert.Equal("<p>x</p>", calls[1].Arguments!["content"]);
    }

    [Fact]
    public void Xml_invoke_cut_off_mid_parameter_keeps_the_call_and_drops_the_unfinished_parameter()
    {
        var contents = ClaudeCodeChatClient.ParseToolCalls(
            "<invoke name=\"SetPocContent\">\n<parameter name=\"appName\">A</parameter>\n<parameter name=\"content\"><section>dở");

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(contents));
        Assert.Equal("SetPocContent", call.Name);
        Assert.Equal(["appName"], call.Arguments!.Keys);
    }

    [Theory]
    [InlineData("<tool_call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.md", "read_file")]
    [InlineData("<tool_call>not json", ClaudeCodeChatClient.InvalidToolCallName)]
    public void Broken_tool_call_still_becomes_a_call_so_the_model_gets_an_error_back(string text, string expectedName)
    {
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(ClaudeCodeChatClient.ParseToolCalls(text)));
        Assert.Equal(expectedName, call.Name);
        Assert.Empty(call.Arguments!);
    }

    [Fact]
    public async Task Structured_output_tool_input_is_returned_as_text()
    {
        var client = new ClaudeCodeChatClient(Model, new FakeRunner(
            Event("""{"type":"message_start","message":{"id":"msg_1","model":"m","usage":{"input_tokens":5,"output_tokens":1}}}"""),
            Event("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"StructuredOutput","input":{}}}"""),
            Event("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\": \"Pa"}}"""),
            Event("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"ris\"}"}}"""),
            Event("""{"type":"content_block_stop","index":0}"""),
            Event("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}"""),
            """{"type":"result","is_error":false,"result":""}"""));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "x")], new ChatOptions { ResponseFormat = ChatResponseFormat.Json });

        Assert.Equal("{\"city\": \"Paris\"}", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task Cli_error_result_fails_the_call_with_its_message()
    {
        var client = new ClaudeCodeChatClient(Model, new FakeRunner(
            """{"type":"result","is_error":true,"api_error_status":404,"result":"There's an issue with the selected model."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains("selected model", ex.Message);
    }

    // HTTPS_PROXY của máy công ty thường trỏ thẳng proxy đòi NTLM mà CLI không trả lời được: biến khai ở
    // Llm:ClaudeCode:Environment phải ĐÈ được nó, kể cả bản khác hoa thường (Linux coi là hai biến riêng).
    [Fact]
    public void ApplyEnvironment_OverridesInheritedVariables_AndEmptyValueRemovesThem()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HTTPS_PROXY"] = "http://rb-proxy-apac.bosch.com:8080",
            ["https_proxy"] = "http://rb-proxy-apac.bosch.com:8080",
            ["HTTP_PROXY"] = "http://rb-proxy-apac.bosch.com:8080",
            ["PATH"] = "/usr/bin",
        };

        ClaudeCodeCli.ApplyEnvironment(target, new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = "http://127.0.0.1:3128",
            ["HTTP_PROXY"] = "",
        });

        Assert.Equal("http://127.0.0.1:3128", target["HTTPS_PROXY"]);
        Assert.False(target.ContainsKey("https_proxy"));
        Assert.False(target.ContainsKey("HTTP_PROXY"));
        Assert.Equal("/usr/bin", target["PATH"]);
    }

    // App khởi động từ bên trong một phiên Claude Code thừa hưởng biến của phiên đó; CLI của app thấy
    // CLAUDE_CODE_ENTRYPOINT/CLAUDE_AGENT_SDK_VERSION thì mỗi lời gọi kéo theo một request "đặt tên phiên" chở lại
    // cả nội dung lượt đó. Gỡ đúng danh sách đó — cấu hình thật (CLAUDE_CONFIG_DIR) thì giữ, và khai lại được.
    [Fact]
    public void PrepareEnvironment_DropsParentSessionVariables_ButKeepsMachineConfig()
    {
        var target = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode",
            ["CLAUDE_AGENT_SDK_VERSION"] = "1.0",
            ["claudecode"] = "1",
            ["CLAUDE_EFFORT"] = "high",
            ["CLAUDE_CONFIG_DIR"] = @"D:\claude",
            ["PATH"] = "/usr/bin",
        };

        ClaudeCodeCli.PrepareEnvironment(target, new Dictionary<string, string> { ["CLAUDE_EFFORT"] = "low" });

        Assert.Equal(new[] { "CLAUDE_CONFIG_DIR", "CLAUDE_EFFORT", "PATH" }, target.Keys.OrderBy(k => k));
        Assert.Equal("low", target["CLAUDE_EFFORT"]);
    }

    // ── Phiên giữ sống (vòng lặp agent) ──────────────────────────────────────────────────────────────

    private sealed class FakeSession(Queue<string[]> turns) : IClaudeCodeSession
    {
        public readonly List<string> Sent = new();
        public bool Disposed;

        public async IAsyncEnumerable<string> SendAsync(string stdinLine, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Sent.Add(stdinLine);
            foreach (var line in turns.Dequeue())
            {
                await Task.Yield();
                yield return line;
            }
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePool(params string[][] turns) : IClaudeCodeSessionPool
    {
        private readonly Queue<string[]> _turns = new(turns);
        private readonly Dictionary<string, IClaudeCodeSession> _kept = new();
        public readonly List<FakeSession> Opened = new();
        public readonly List<ClaudeCodeInvocation> OpenedWith = new();

        public IClaudeCodeSession? Take(string key) => _kept.Remove(key, out var s) ? s : null;

        public IClaudeCodeSession Open(ClaudeCodeInvocation invocation)
        {
            OpenedWith.Add(invocation);
            var session = new FakeSession(_turns);
            Opened.Add(session);
            return session;
        }

        public void Keep(string key, IClaudeCodeSession session) => _kept[key] = session;
        public int KeptCount => _kept.Count;
    }

    private sealed class PoolRunner(FakePool pool) : IClaudeCodeRunner
    {
        public IAsyncEnumerable<string> RunAsync(ClaudeCodeInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Lượt có tool phải đi qua phiên giữ sống.");

        public IClaudeCodeSessionPool Sessions => pool;
    }

    private static string StdinText(string stdinLine) => string.Concat(
        JsonNode.Parse(stdinLine)!["message"]!["content"]!.AsArray().Select(b => b!["text"]?.GetValue<string>() ?? ""));

    private static string[] ToolCallTurn(string path) =>
        TextTurn("<tool_call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"" + path + "\"}}</tool_call>");

    [Fact]
    public async Task Agent_loop_continues_the_live_session_and_sends_only_the_new_tool_results()
    {
        var pool = new FakePool(ToolCallTurn("a.md"), TextTurn("Xong."));
        var client = new ClaudeCodeChatClient(Model, new PoolRunner(pool));
        var history = new List<ChatMessage> { new(ChatRole.User, "Đọc a.md rồi báo lại.") };

        // Bước 1: mở phiên, gửi lượt đầu nguyên văn; trả lời có gọi tool ⇒ phiên được cất lại.
        var first = await client.GetResponseAsync(history, WithTool());
        history.AddRange(first.Messages);
        var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "nội dung a")]));
        Assert.Equal(1, pool.KeptCount);

        // Bước 2: cùng phiên, CHỈ gửi kết quả tool — không kể lại lịch sử, không mở tiến trình mới.
        var second = await client.GetResponseAsync(history, WithTool());

        Assert.Equal("Xong.", second.Text);
        var session = Assert.Single(pool.Opened);
        Assert.Equal(2, session.Sent.Count);
        var continuation = StdinText(session.Sent[1]);
        Assert.Contains("<tool_result id=\"" + call.CallId + "\">\nnội dung a", continuation);
        Assert.DoesNotContain("<turn", continuation);
        Assert.DoesNotContain("Đọc a.md", continuation);
        // Lượt cuối không gọi tool ⇒ xong việc: phiên bị đóng, không còn gì trong kho.
        Assert.True(session.Disposed);
        Assert.Equal(0, pool.KeptCount);
        // Lượt đầu là một lượt user trần: system không mang chỉ dẫn bản ghi.
        Assert.DoesNotContain("## Định dạng hội thoại", pool.OpenedWith.Single().SystemPrompt);
    }

    [Fact]
    public async Task Changed_history_does_not_resume_a_session_and_replays_the_transcript_instead()
    {
        var pool = new FakePool(ToolCallTurn("a.md"), TextTurn("Xong."));
        var client = new ClaudeCodeChatClient(Model, new PoolRunner(pool));
        var history = new List<ChatMessage> { new(ChatRole.User, "Đọc a.md.") };
        var first = await client.GetResponseAsync(history, WithTool());

        // Lịch sử không khớp phiên đã cất (lượt assistant bị thay) ⇒ mở phiên mới, kể lại bằng bản ghi.
        var changed = new List<ChatMessage>
        {
            history[0],
            new(ChatRole.Assistant, [new FunctionCallContent("khác", "read_file", new Dictionary<string, object?> { ["path"] = "a.md" })]),
            new(ChatRole.Tool, [new FunctionResultContent("khác", "nội dung a")]),
        };
        await client.GetResponseAsync(changed, WithTool());

        Assert.Equal(2, pool.Opened.Count);
        Assert.Contains("<turn role=\"assistant\">", StdinText(pool.Opened[1].Sent.Single()));
        Assert.Contains("## Định dạng hội thoại", pool.OpenedWith[1].SystemPrompt);
    }

    [Fact]
    public async Task Calls_without_tools_never_use_the_session_pool()
    {
        var runner = new FakeRunner(TextTurn("chào"));
        var client = new ClaudeCodeChatClient(Model, runner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("chào", response.Text);
        Assert.NotNull(runner.Invocation);
    }

    [Fact]
    public void Session_pool_evicts_the_oldest_over_capacity_and_idle_sessions_after_timeout()
    {
        var opened = new List<FakeSession>();
        using var pool = new ClaudeCodeSessionPool(_ => { var s = new FakeSession(new Queue<string[]>()); opened.Add(s); return s; });

        var sessions = Enumerable.Range(0, ClaudeCodeSessionPool.MaxSessions + 1)
            .Select(i => (Key: "k" + i, Session: (FakeSession)pool.Open(null!)))
            .ToList();
        foreach (var (key, session) in sessions)
            pool.Keep(key, session);

        Assert.Equal(ClaudeCodeSessionPool.MaxSessions, pool.IdleCount);
        Assert.True(sessions[0].Session.Disposed);
        Assert.Null(pool.Take("k0"));

        var taken = pool.Take("k1");
        Assert.Same(sessions[1].Session, taken);

        pool.Sweep(DateTime.UtcNow + ClaudeCodeSessionPool.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(0, pool.IdleCount);
        Assert.All(sessions.Skip(2), s => Assert.True(s.Session.Disposed));
        Assert.False(sessions[1].Session.Disposed);
    }
}
