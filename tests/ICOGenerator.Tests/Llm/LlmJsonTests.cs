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

    // ── Thẻ tham số tool lọt vào JSON ────────────────────────────────────────────────────────────────
    // claude-sonnet-5-5 trả câu trả lời qua tool structured_response và thỉnh thoảng viết object lồng bằng
    // cú pháp thẻ của lời gọi tool: chuỗi mở đầu bằng <parameter name="content"> ở chỗ schema đòi object.

    private sealed class Doc
    {
        public string? Title { get; set; }
        public Body? Body { get; set; }
    }

    private sealed class Body
    {
        public string? Content { get; set; }
    }

    [Fact]
    public void TryDeserialize_RebuildsNestedObject_FromLeakedParameterTag()
    {
        var value = LlmJson.TryDeserialize<Doc>(
            """{"title":"t","body":"\n<parameter name=\"content\"># Tiêu đề\n- ý một</parameter>\n"}""");

        Assert.Equal("t", value?.Title);
        Assert.Equal("# Tiêu đề\n- ý một", value?.Body?.Content);
    }

    // Chỉ sửa đúng một hình dạng: thẻ ở ĐẦU chuỗi. Một chuỗi nhắc tới thẻ ở giữa nội dung là dữ liệu thật.
    [Fact]
    public void TryDeserialize_LeavesTagInsideStringContentAlone()
    {
        var value = LlmJson.TryDeserialize<Reply>("""{"answer":"viết <parameter name=\"x\"> là sai"}""");

        Assert.Equal("viết <parameter name=\"x\"> là sai", value?.Answer);
    }

    // ── Câu trả lời được viết lại sau một bản hỏng ───────────────────────────────────────────────────

    [Fact]
    public void TryDeserialize_UsesLaterObject_WhenTheFirstIsBroken()
    {
        var text = "{\"answer\": \n<parameter name=\"x\">thô, \"không\" escape\n}\n{\"answer\":\"bản viết lại\",\"score\":2}";

        var value = LlmJson.TryDeserialize<Reply>(text, requireKnownProperty: true);

        Assert.Equal("bản viết lại", value?.Answer);
        Assert.Equal(2, value?.Score);
    }

    [Fact]
    public void TryDeserialize_KeepsTheFirstObject_WhenItIsUsable()
    {
        var value = LlmJson.TryDeserialize<Reply>("{\"answer\":\"một\"}\n{\"answer\":\"hai\"}");

        Assert.Equal("một", value?.Answer);
    }

    // Object con nằm giữa dòng không phải một câu trả lời viết lại — không bóc nhầm.
    [Fact]
    public void TryDeserialize_DoesNotPickAnInlineObject_AfterABrokenOne()
    {
        Assert.Null(LlmJson.TryDeserialize<Reply>("{\"answer\": } trong khi {\"answer\":\"lạc\"}", requireKnownProperty: true));
    }
}
