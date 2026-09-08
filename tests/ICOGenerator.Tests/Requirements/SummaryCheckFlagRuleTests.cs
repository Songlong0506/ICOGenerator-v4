using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cờ `summaryCheck` do MODEL khai, parser không đoán hộ — cùng đường đã đi với `openEnded` và
// `multiSelect` sau khi ShapeAnswer/LooksOpenEnded bị gỡ.
//
// Trước đây nhịp tóm tắt được nhận ra bằng một bảng cụm từ tiếng Việt ("tóm tắt lại", "mình hiểu đúng"…)
// cộng một dấu hỏi. Bảng ấy không bao giờ phủ hết, mà bắt quá tay thì gắn bộ chip xác nhận vào một câu
// hỏi khai thác thật — người dùng bấm "Đúng rồi, tiếp tục" và điều BA vừa hỏi không bao giờ được trả lời.
//
// Đổi lại, prompt phải DẠY model đặt cờ, nếu không lượt tóm tắt mất chip. Hai test dưới giữ đúng hai đầu
// đó: parser đọc được cờ, và prompt còn dạy nó.
public class SummaryCheckFlagRuleTests
{
    [Fact]
    public void ParserReadsTheFlagStraightFromTheModel()
    {
        var reply = new BAChatReplyParser().Parse(
            """{"message":"Mình hiểu vậy đã đúng chưa ạ?","summaryCheck":true,"suggestions":[]}""");

        Assert.True(reply.SummaryCheck);
    }

    // Không có cờ ⇒ false, kể cả khi lời văn đúng hình dạng một bản tóm tắt: parser KHÔNG đoán.
    [Fact]
    public void ParserDoesNotInferTheFlagFromTheWording()
    {
        var reply = new BAChatReplyParser().Parse(
            """{"message":"Mình xin tóm tắt lại những gì đã chốt. Mình hiểu đúng chứ ạ?","suggestions":[]}""");

        Assert.False(reply.SummaryCheck);
    }

    [Fact]
    public void ChatPromptTeachesTheFlag()
    {
        var prompt = PromptFixture.Read("BusinessAnalyst/requirement-chat.v4.md");

        Assert.Contains("summaryCheck", prompt, StringComparison.Ordinal);
        // Và nói rõ ranh giới: chỉ đúng nhịp phát-lại-xin-gật, không phải mọi lượt có chữ "tóm tắt".
        Assert.Contains("summaryCheck: false", prompt, StringComparison.Ordinal);
    }
}
