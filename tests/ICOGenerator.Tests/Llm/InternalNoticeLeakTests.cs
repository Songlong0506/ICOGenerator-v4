using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Lượt phải gửi lại mà BỎ ảnh mang theo một dòng dặn dò dành riêng cho model ("bạn không nhìn thấy hình
// nào, đừng suy đoán"). Nó nằm trong lượt user nên model hoàn toàn có thể chép nguyên văn vào câu trả lời
// — và đã chép thật: người dùng nghiệp vụ đọc phải một dòng lệnh kỹ thuật nằm giữa bản tóm tắt tài liệu của
// chính họ. Các test này khóa cả hai lớp chặn: lời cấm chép gửi cho model, và lưới dọn ở đầu ra.
public class InternalNoticeLeakTests
{
    private static string NoticeSentToModel()
    {
        var messages = EndpointQuirks.WithoutImageContent(new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent("nội dung tài liệu"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            })
        });
        return messages[0].Text;
    }

    [Fact]
    public void TheNoticeTellsTheModelNotToRepeatIt()
    {
        var text = NoticeSentToModel();

        Assert.Contains("KHÔNG nhìn thấy hình nào", text);
        // Câu cấm chép là lớp chặn CHÍNH; thiếu nó thì lưới dọn ở dưới phải làm việc mỗi lượt.
        Assert.Contains("không chép", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripInternalNotices_RemovesTheLineTheModelCopiedBack()
    {
        var leaked = "Đây là tài liệu kỹ thuật cho hệ thống quản lý belt.\n"
            + "- 3.2 Screens Definition\n"
            + NoticeSentToModel().Split('\n').First(l => l.Contains("GHI CHÚ NỘI BỘ")) + "\n"
            + "- Chỗ chưa chắc: chưa đọc được phần hình.";

        var cleaned = EndpointQuirks.StripInternalNotices(leaked);

        Assert.DoesNotContain("GHI CHÚ NỘI BỘ", cleaned);
        // Chỉ dọn đúng dòng bị rò — phần BA thật sự viết phải còn nguyên.
        Assert.Contains("3.2 Screens Definition", cleaned);
        Assert.Contains("Chỗ chưa chắc", cleaned);
    }

    [Fact]
    public void StripInternalNotices_LeavesAnOrdinaryReplyUntouched()
    {
        const string reply = "Mình đọc được quy trình quản lý belt.\n- Belt Type Setting\nĐúng chưa ạ?";
        Assert.Equal(reply, EndpointQuirks.StripInternalNotices(reply));
    }
}
