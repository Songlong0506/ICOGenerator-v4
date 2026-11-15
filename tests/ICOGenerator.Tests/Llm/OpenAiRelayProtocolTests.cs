using System.Text.Json;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Relay OpenAI-compatible cho agent CLI bên ngoài (opencode): phải đọc đúng một vòng tool của agent lập trình
// (lời gọi tool của lượt trước + kết quả tool) và trả chunk đúng hình mà AI SDK ghép lại được.
public class OpenAiRelayProtocolTests
{
    [Fact]
    public void ParsesAToolLoopTurn()
    {
        using var body = JsonDocument.Parse("""
        {
          "model": "poc", "stream": true, "max_tokens": 4096, "temperature": 0.2,
          "messages": [
            { "role": "system", "content": "Bạn là Developer." },
            { "role": "user", "content": [ { "type": "text", "text": "Dựng POC" } ] },
            { "role": "assistant", "content": null, "tool_calls": [
              { "id": "call_1", "type": "function", "function": { "name": "write", "arguments": "{\"filePath\":\"a.js\",\"content\":\"x\"}" } } ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "ok" }
          ],
          "tools": [ { "type": "function", "function": { "name": "write", "description": "Ghi file",
            "parameters": { "type": "object", "properties": { "filePath": { "type": "string" } } } } } ],
          "tool_choice": "auto"
        }
        """);

        var request = OpenAiRelayProtocol.TryParseRequest(body.RootElement, out var error);

        Assert.NotNull(request);
        Assert.Equal(string.Empty, error);
        Assert.True(request!.Stream);
        Assert.Equal(4096, request.Options.MaxOutputTokens);
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal(ChatRole.System, request.Messages[0].Role);
        Assert.Equal("Dựng POC", request.Messages[1].Text);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(request.Messages[2].Contents));
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("write", call.Name);
        Assert.Equal("a.js", ((JsonElement)call.Arguments!["filePath"]!).GetString());

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(request.Messages[3].Contents));
        Assert.Equal("call_1", result.CallId);

        var tool = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(request.Options.Tools!));
        Assert.Equal("write", tool.Name);
        Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
        Assert.Equal(ChatToolMode.Auto, request.Options.ToolMode);
    }

    [Fact]
    public void RejectsARequestWithoutMessages_OrAToolResultWithoutItsCallId()
    {
        using var empty = JsonDocument.Parse("""{"model":"poc"}""");
        Assert.Null(OpenAiRelayProtocol.TryParseRequest(empty.RootElement, out var error));
        Assert.Contains("messages", error);

        using var orphan = JsonDocument.Parse("""{"messages":[{"role":"tool","content":"ok"}]}""");
        Assert.Null(OpenAiRelayProtocol.TryParseRequest(orphan.RootElement, out error));
        Assert.Contains("tool_call_id", error);
    }

    [Fact]
    public void StreamsTextThenToolCalls_WithRunningIndexes_AndFinishesWithToolCalls()
    {
        var toolIndex = 0;
        var roleSent = false;

        var text = OpenAiRelayProtocol.UpdateChunk("id", "m", 1, new ChatResponseUpdate(ChatRole.Assistant, "Xin chào"), ref toolIndex, ref roleSent);
        using (var doc = JsonDocument.Parse(text!))
        {
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            Assert.Equal("assistant", delta.GetProperty("role").GetString());
            Assert.Equal("Xin chào", delta.GetProperty("content").GetString());
        }

        var calls = new ChatResponseUpdate(ChatRole.Assistant,
        [
            new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["filePath"] = "a" }),
            new FunctionCallContent("c2", "bash", new Dictionary<string, object?> { ["command"] = "node --check a.js" })
        ]);
        var chunk = OpenAiRelayProtocol.UpdateChunk("id", "m", 1, calls, ref toolIndex, ref roleSent);
        using (var doc = JsonDocument.Parse(chunk!))
        {
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            Assert.False(delta.TryGetProperty("role", out _)); // role chỉ ở chunk đầu
            var toolCalls = delta.GetProperty("tool_calls");
            Assert.Equal(0, toolCalls[0].GetProperty("index").GetInt32());
            Assert.Equal(1, toolCalls[1].GetProperty("index").GetInt32());
            Assert.Equal("bash", toolCalls[1].GetProperty("function").GetProperty("name").GetString());
            using var args = JsonDocument.Parse(toolCalls[1].GetProperty("function").GetProperty("arguments").GetString()!);
            Assert.Equal("node --check a.js", args.RootElement.GetProperty("command").GetString());
        }

        Assert.Equal("tool_calls", OpenAiRelayProtocol.FinishReason(ChatFinishReason.Stop, hasToolCalls: toolIndex > 0));
        Assert.Equal("length", OpenAiRelayProtocol.FinishReason(ChatFinishReason.Length, hasToolCalls: false));
    }

    [Fact]
    public void UsageOnlyUpdates_ProduceNoContentChunk()
    {
        var toolIndex = 0;
        var roleSent = false;
        var update = new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 5 })]);
        Assert.Null(OpenAiRelayProtocol.UpdateChunk("id", "m", 1, update, ref toolIndex, ref roleSent));

        using var usage = JsonDocument.Parse(OpenAiRelayProtocol.UsageChunk("id", "m", 1, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2 }));
        Assert.Equal(0, usage.RootElement.GetProperty("choices").GetArrayLength());
        Assert.Equal(7, usage.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }
}
