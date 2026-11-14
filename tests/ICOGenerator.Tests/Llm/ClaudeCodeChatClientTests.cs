using System.ComponentModel;
using System.Runtime.CompilerServices;
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
}
