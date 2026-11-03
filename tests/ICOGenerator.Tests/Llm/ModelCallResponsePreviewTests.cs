using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Lượt agent toàn tool call không có chữ nào: tab Response từng trống trơn dù model đã sinh 60k token, đọc ra
// thành "tham số gửi sai". Phải thấy được model đã gọi gì và call nào bị cụt.
public class ModelCallResponsePreviewTests
{
    [Fact]
    public void TextOnly_IsUnchanged() =>
        Assert.Equal("xin chào", ModelCallResponsePreview.Build("xin chào", []));

    [Fact]
    public void ToolCalls_AreListed_AndACutCallIsFlagged()
    {
        var preview = ModelCallResponsePreview.Build("", [
            new FunctionCallContent("1", "AppendPocContent", new Dictionary<string, object?> { ["content"] = "<section>" }),
            new FunctionCallContent("2", "SetPocScript", new Dictionary<string, object?>()),
        ]);

        Assert.Contains("→ tool call AppendPocContent", preview);
        Assert.Contains("<section>", preview);
        Assert.Contains("→ tool call SetPocScript — KHÔNG có đối số", preview);
    }

    [Fact]
    public void LongArguments_AreTrimmed()
    {
        var preview = ModelCallResponsePreview.Build("", [
            new FunctionCallContent("1", "SetPocContent", new Dictionary<string, object?> { ["content"] = new string('x', 50_000) }),
        ]);

        Assert.True(preview.Length < ModelCallResponsePreview.MaxArgumentChars + 200);
        Assert.Contains("cắt bớt", preview);
    }
}
