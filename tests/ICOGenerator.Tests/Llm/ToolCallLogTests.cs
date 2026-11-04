using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Lượt agent chỉ gọi tool có phần chữ RỖNG — trước đây popup AI Call Logs hiện Response trống cho cả dãy bước
// SetPocContent/AppendPocScript/AuditPocContent, trông như chúng không làm gì.
public class ToolCallLogTests
{
    [Fact]
    public void ToolCalls_AreWrittenIntoTheResponse_WithTheirArguments()
    {
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("c1", "SetPocContent", new Dictionary<string, object?> { ["content"] = "<section>Đơn nghỉ phép</section>", ["appName"] = "Leave" }),
            new FunctionCallContent("c2", "AuditPocContent")
        ]);

        var text = ToolCallLog.Append(string.Empty, [message]);

        Assert.Contains("→ Gọi tool SetPocContent", text);
        Assert.Contains("<section>Đơn nghỉ phép</section>", text); // không escape tiếng Việt / dấu < >
        Assert.Contains("→ Gọi tool AuditPocContent()", text);
    }

    [Fact]
    public void LongArguments_AreShortened_AndPlainTextIsUnchanged()
    {
        var huge = new string('x', 50_000);
        var message = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "AppendPocScript", new Dictionary<string, object?> { ["script"] = huge })]);

        var text = ToolCallLog.Append("Đang nối script.", [message]);

        Assert.StartsWith("Đang nối script.", text);
        Assert.Contains("cắt bớt — tổng 50000 ký tự", text);
        Assert.True(text.Length < 25_000);
        Assert.Equal("Chỉ có chữ.", ToolCallLog.Append("Chỉ có chữ.", [new ChatMessage(ChatRole.Assistant, "Chỉ có chữ.")]));
    }
}
