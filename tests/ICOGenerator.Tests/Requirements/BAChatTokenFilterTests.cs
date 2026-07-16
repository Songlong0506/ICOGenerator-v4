using System.Text;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BAChatTokenFilter biến luồng token thô (JSON {"message": ...} hoặc text thuần) thành phần text
// hiển thị được để stream "BA đang gõ" lên UI. Điểm khó là delta có thể cắt NGANG bất cứ đâu —
// giữa khóa "message", giữa một escape \n, giữa \uXXXX — nên test bẻ input theo nhiều cỡ mảnh
// để chốt máy trạng thái không phụ thuộc ranh giới token.
public class BAChatTokenFilterTests
{
    private static string FeedAll(string raw, int chunkSize = int.MaxValue)
    {
        var output = new StringBuilder();
        var filter = new BAChatTokenFilter(s => output.Append(s));

        for (var i = 0; i < raw.Length; i += chunkSize)
            filter.Feed(raw.Substring(i, Math.Min(chunkSize, raw.Length - i)));

        return output.ToString();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Json_EmitsOnlyMessageValue(int chunkSize)
    {
        const string raw = """{"message": "Đối tượng dùng chính là ai?", "suggestions": ["Nhân viên", "Quản lý"]}""";

        Assert.Equal("Đối tượng dùng chính là ai?", FeedAll(raw, chunkSize));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2)]
    public void PlainText_PassesThroughVerbatim(int chunkSize)
    {
        const string raw = "Chào bạn!\nMình cần thêm thông tin về quy trình duyệt.";

        Assert.Equal(raw, FeedAll(raw, chunkSize));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(4)]
    public void FencedJson_IsUnwrapped(int chunkSize)
    {
        const string raw = "```json\n{\"message\": \"Xin chào\"}\n```";

        Assert.Equal("Xin chào", FeedAll(raw, chunkSize));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public void Escapes_AreUnescaped_AcrossChunkBoundaries(int chunkSize)
    {
        const string raw = """{"message": "Dòng 1\nDòng 2 \"quote\" tab\tvà unicode đúng"}""";

        Assert.Equal("Dòng 1\nDòng 2 \"quote\" tab\tvà unicode đúng", FeedAll(raw, chunkSize));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public void UnicodeEscape_IsDecoded_AcrossChunkBoundaries(int chunkSize)
    {
        // đ = 'đ', ú = 'ú' — escape phải được giải mã kể cả khi 4 hex digit bị cắt giữa hai delta.
        Assert.Equal("đúng", FeedAll("{\"message\": \"\\u0111\\u00fang\"}", chunkSize));
    }

    [Fact]
    public void LeadingWhitespace_BeforeJson_IsIgnored()
    {
        Assert.Equal("OK", FeedAll("  \n\t{\"message\":\"OK\"}"));
    }

    [Fact]
    public void MessageKey_CaseInsensitive()
    {
        Assert.Equal("OK", FeedAll("""{"Message": "OK"}"""));
    }

    [Fact]
    public void MessageAfterOtherFields_IsStillFound()
    {
        Assert.Equal("Câu hỏi?", FeedAll("""{"ready": false, "message": "Câu hỏi?"}"""));
    }

    [Fact]
    public void JsonWithoutMessage_EmitsNothing()
    {
        Assert.Equal("", FeedAll("""{"suggestions": ["A", "B"]}"""));
    }

    [Fact]
    public void NonStringMessage_EmitsNothing()
    {
        Assert.Equal("", FeedAll("""{"message": null, "suggestions": []}"""));
    }

    [Fact]
    public void ContentAfterClosingQuote_IsSwallowed()
    {
        const string raw = """{"message": "Hết rồi", "suggestions": ["không stream cái này"]}""";

        Assert.Equal("Hết rồi", FeedAll(raw, 5));
    }

    [Fact]
    public void PlainText_StartingWithSingleBacktick_IsPlain()
    {
        const string raw = "`RunCommand` là tool chạy lệnh.";

        Assert.Equal(raw, FeedAll(raw, 2));
    }
}
