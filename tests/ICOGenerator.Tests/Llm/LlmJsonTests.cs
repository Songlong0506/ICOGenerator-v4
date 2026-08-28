using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// LlmJson là chỗ nghẽn CHUNG của mọi đường đọc JSON model trả về (structured output của LlmClient + gần
// chục parser dự phòng ở Services/Requirements và Evals). Trước đây mỗi nơi chép tay "bóc JSON rồi
// deserialize trong try/catch" nên hành vi biên không ai khoá; các test dưới đây khoá đúng phần đó.
public class LlmJsonTests
{
    private sealed class Reply
    {
        public string? Answer { get; set; }
        public int Score { get; set; }
    }

    // ── ExtractObject ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractObject_StripsCodeFence()
    {
        var text = "```json\n{\"answer\":\"ok\"}\n```";

        Assert.Equal("{\"answer\":\"ok\"}", LlmJson.ExtractObject(text));
    }

    [Fact]
    public void ExtractObject_IgnoresProseAroundTheObject()
    {
        var text = "Đây là kết quả bạn cần:\n{\"answer\":\"ok\"}\nHy vọng giúp được bạn.";

        Assert.Equal("{\"answer\":\"ok\"}", LlmJson.ExtractObject(text));
    }

    [Fact]
    public void ExtractObject_IgnoresBracesInsideStrings()
    {
        var text = """{"answer":"có dấu } và \" bên trong"}""";

        Assert.Equal(text, LlmJson.ExtractObject(text));
    }

    // Phản hồi bị cắt giữa chừng (chạm trần token) — trả rỗng thay vì nửa object, để caller rơi về
    // fallback thay vì nhận dữ liệu thiếu mà tưởng là đủ.
    [Theory]
    [InlineData("{\"answer\":\"ok\"")]
    [InlineData("model không trả JSON gì cả")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractObject_ReturnsEmpty_WhenThereIsNoCompleteObject(string? text)
    {
        Assert.Equal(string.Empty, LlmJson.ExtractObject(text));
    }

    // ── TryDeserialize ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryDeserialize_ReadsFieldsCaseInsensitively()
    {
        var value = LlmJson.TryDeserialize<Reply>("""{"ANSWER":"ok","Score":4}""");

        Assert.Equal("ok", value?.Answer);
        Assert.Equal(4, value?.Score);
    }

    [Theory]
    [InlineData("chỉ là văn xuôi")]
    [InlineData("{\"answer\": }")]     // JSON hỏng
    [InlineData("{\"answer\": 12}")]   // đúng tên field nhưng sai kiểu
    [InlineData(null)]
    public void TryDeserialize_ReturnsNull_ForAnythingUnusable(string? text)
    {
        Assert.Null(LlmJson.TryDeserialize<Reply>(text));
    }

    // DÃY THOÁT HỎNG — ca thật đã đốt một lượt chat trên màn hình (dự án JD Libary, lượt 6): model viết
    // tiếng Việt dạng ASCII toàn \uXXXX và nhả `\u1E1y` (rụng một chữ số hex) giữa chữ "vậy". Cả object
    // không đọc được ⇒ BAChatReplyParser rơi về nhánh text thuần ⇒ NGUYÊN KHỐI JSON lên khung chat như
    // một lượt trả lời của BA. Sửa theo hướng MẤT KÝ TỰ, không đoán ký tự: phần còn lại của câu phải
    // nguyên vẹn, chỉ chỗ hỏng là rụng.
    [Fact]
    public void TryDeserialize_RepairsABrokenUnicodeEscape_InsteadOfGivingUp()
    {
        var value = LlmJson.TryDeserialize<Reply>("""{"answer":"ch\u00EDnh x\u00E1c nh\u01B0 v\u1E1y","score":3}""");

        Assert.Equal(3, value?.Score);
        Assert.StartsWith("chính xác như v", value?.Answer);
        Assert.DoesNotContain("\\u", value?.Answer);
    }

    [Fact]
    public void TryDeserialize_RepairsAStrayBackslash()
    {
        // `\x` không phải escape hợp lệ của JSON: bỏ dấu gạch chéo, giữ ký tự.
        var value = LlmJson.TryDeserialize<Reply>("""{"answer":"file C:\xData\\bao-cao.xlsx"}""");

        Assert.Equal("file C:xData\\bao-cao.xlsx", value?.Answer);
    }

    // Sửa dãy thoát KHÔNG được biến thành "sửa mọi thứ": JSON hỏng ở cấu trúc vẫn trả null để caller rơi
    // về parser dự phòng của mình.
    [Fact]
    public void TryDeserialize_StillReturnsNull_WhenTheDamageIsNotAnEscape()
    {
        Assert.Null(LlmJson.TryDeserialize<Reply>("""{"answer": , "score": 3}"""));
    }

    // Mặc định (các parser dự phòng): giữ nguyên hành vi cũ — JSON hợp lệ nhưng toàn field lạ vẫn đọc
    // được thành object mặc-định-hết.
    [Fact]
    public void TryDeserialize_WithoutGuard_AcceptsForeignShape()
    {
        var value = LlmJson.TryDeserialize<Reply>("""{"totally":"different"}""");

        Assert.NotNull(value);
        Assert.Null(value!.Answer);
    }

    // Bật guard (đường structured output): phản hồi phải trùng ít nhất một tên field, nếu không thì trả
    // null để caller dùng parser riêng — thay vì nhận một object rỗng trông như parse thành công.
    [Fact]
    public void TryDeserialize_WithGuard_RejectsForeignShape()
    {
        Assert.Null(LlmJson.TryDeserialize<Reply>("""{"totally":"different"}""", requireKnownProperty: true));
    }

    [Fact]
    public void TryDeserialize_WithGuard_AcceptsWhenAnyFieldMatches()
    {
        var value = LlmJson.TryDeserialize<Reply>("""{"answer":"ok","extra":1}""", requireKnownProperty: true);

        Assert.Equal("ok", value?.Answer);
    }
}
