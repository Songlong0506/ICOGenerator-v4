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

    // Lượt "hết token mà không có chữ nào": phần suy luận ẩn (khi endpoint trả ra) là thứ duy nhất cho biết
    // token đã đi đâu.
    [Fact]
    public void Reasoning_IsWrittenIntoTheResponse()
    {
        var message = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("Lập kế hoạch 12 màn hình…")]);

        var text = ToolCallLog.Append(string.Empty, [message]);

        Assert.Contains("Suy luận ẩn của model", text);
        Assert.Contains("Lập kế hoạch 12 màn hình…", text);
    }

    // Suy luận ẩn không có bản nào khác ngoài call log, và chỗ model sa lầy thường ở CUỐI mạch — nên ghi trọn.
    [Fact]
    public void LongReasoning_IsKeptInFull()
    {
        var reasoning = new string('r', 50_000) + "ĐOẠN CUỐI";
        var message = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(reasoning)]);

        var text = ToolCallLog.Append(string.Empty, [message]);

        Assert.Contains(reasoning, text);
        Assert.DoesNotContain("cắt bớt", text);
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
