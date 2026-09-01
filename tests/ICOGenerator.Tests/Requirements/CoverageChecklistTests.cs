using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Khung rỗng của panel "Tiến độ khai thác" (dự án vừa tạo / vừa New Chat) được bóc từ chính prompt
// requirement-coverage.v4.md — cùng file quy định 12 dòng mà LLM phải xuất. Test này là chốt chặn cho
// giao kèo đó: đổi tên/thứ tự nhóm hay format khối template trong prompt mà khung không bóc ra được nữa
// thì fail ở đây, thay vì âm thầm làm panel trống trơn ở màn hình dự án mới.
public class CoverageChecklistTests
{
    [Fact]
    public void Parse_RealCoveragePrompt_YieldsTwelveUnaskedGroups()
    {
        var items = CoverageChecklist.Parse(CoveragePromptFixture.Read());

        Assert.Equal(12, items.Count);
        Assert.All(items, x =>
        {
            Assert.Equal("CHƯA HỎI", x.Status);
            Assert.False(string.IsNullOrWhiteSpace(x.Label));
            // Placeholder "<tóm tắt …>" của prompt phải bị xoá: dòng CHƯA HỎI không có gì để tóm tắt, mà
            // nó lại được render thẳng vào tooltip của dòng trên panel.
            Assert.Equal(string.Empty, x.Summary);
            Assert.Equal(string.Empty, x.Evidence);
        });

        // Ba nhóm ★ (cốt lõi) đứng đầu — thứ tự này là thứ tự BA ưu tiên hỏi, panel hiện đúng như vậy.
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.Equal(3, items.Count(x => x.IsCore));
        Assert.All(items.Take(3), x => Assert.True(x.IsCore));
    }

    // Bản đồ thật sau lượt chat đầu tiên phải khớp khung rỗng từng nhãn một, nếu không danh sách sẽ
    // "nhảy chữ" ngay trước mắt người dùng khi lượt đầu tiên trả về.
    [Fact]
    public void Parse_LabelsMatchWhatTheDistillerIsToldToEmit()
    {
        var prompt = CoveragePromptFixture.Read();
        var skeleton = CoverageChecklist.Parse(prompt);

        // Bản đồ "thật" mô phỏng: cùng khối template nhưng đã điền trạng thái, như model sẽ trả về.
        var realMap = string.Join("\n", skeleton.Select(x =>
            $"- {(x.IsCore ? "★ " : "")}{x.Label}: [RÕ] tóm tắt gì đó"));
        var parsed = CoverageMapParser.Parse(realMap);

        Assert.Equal(
            skeleton.Select(x => x.Label).ToArray(),
            parsed.Select(x => x.Label).ToArray());
        Assert.Equal(
            skeleton.Select(x => x.IsCore).ToArray(),
            parsed.Select(x => x.IsCore).ToArray());
    }

    // Prompt bị sửa hỏng (Prompt Studio) ⇒ khung rỗng, panel ẩn đúng như hành vi trước đây; không ném lỗi
    // giữa đường render trang.
    [Fact]
    public void Parse_WithoutTemplateBlock_ReturnsEmpty()
    {
        Assert.Empty(CoverageChecklist.Parse(null));
        Assert.Empty(CoverageChecklist.Parse("Không còn khối template nào ở đây."));
        // Khối ví dụ (trạng thái thật, không phải placeholder) KHÔNG được nhận nhầm là khung.
        Assert.Empty(CoverageChecklist.Parse("```\n- ★ Mục tiêu / bài toán: [RÕ] ví dụ\n```"));
    }

}
