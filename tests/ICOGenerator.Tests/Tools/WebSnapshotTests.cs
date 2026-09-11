using ICOGenerator.Services.Browser;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Bản đồ điều khiển là ĐỊA CHỈ mà model dùng để bấm, nên format của nó là một hợp đồng: số phải khớp
// data-ico-ref, và mọi lần cắt bớt phải nói ra — im lặng cắt sẽ khiến model kết luận "trang không có
// nút đó" trong khi nút nằm ngay dưới trần.
public class WebSnapshotTests
{
    private static WebControl Ctl(int i, string kind = "button", string label = "Nút", string value = "") =>
        new(i, kind, label, value);

    [Fact]
    public void Render_NumbersControls_AndShowsValues()
    {
        var text = WebSnapshot.Render("https://x.test/", "Trang thử", new[]
        {
            Ctl(1, "textbox", "Tìm kiếm", "khách sạn đà nẵng"),
            Ctl(2, "button", "Tìm")
        }, maxControls: 40);

        Assert.Contains("URL: https://x.test/", text);
        Assert.Contains("[1] textbox \"Tìm kiếm\" = \"khách sạn đà nẵng\"", text);
        Assert.Contains("[2] button \"Tìm\"", text);
    }

    [Fact]
    public void Render_SaysHowManyWereTrimmed()
    {
        var controls = Enumerable.Range(1, 50).Select(i => Ctl(i)).ToList();

        var text = WebSnapshot.Render("https://x.test/", "t", controls, maxControls: 10);

        Assert.Contains("[10] button", text);
        Assert.DoesNotContain("[11] button", text);
        Assert.Contains("còn 40 điều khiển nữa bị cắt", text);
    }

    [Fact]
    public void Render_FlattensMultilineLabels()
    {
        // Nhãn nhiều dòng làm vỡ luật "một điều khiển một dòng": model sẽ đọc phần đuôi như một điều
        // khiển riêng không có số.
        var text = WebSnapshot.Render("https://x.test/", "t", new[] { Ctl(1, label: "Đặt\nphòng\tngay") }, 40);

        Assert.Contains("[1] button \"Đặt phòng ngay\"", text);
        Assert.Equal(4, text.Split('\n').Length);
    }

    [Fact]
    public void Render_ClipsLongLabels()
    {
        var text = WebSnapshot.Render("https://x.test/", "t", new[] { Ctl(1, label: new string('a', 200)) }, 40);

        Assert.Contains("…", text);
        Assert.DoesNotContain(new string('a', 100), text);
    }

    [Fact]
    public void Render_TellsModelWhatToDo_WhenPageHasNoControls()
    {
        var text = WebSnapshot.Render("https://x.test/", "t", Array.Empty<WebControl>(), 40);

        Assert.Contains("ReadPage", text);
    }

    [Fact]
    public void ClipPageText_KeepsHeadAndTail()
    {
        // Cắt cụt đuôi là cách chắc chắn đánh rơi phần kết quả (bảng giá, tổng cộng) vốn hay nằm cuối trang.
        var text = new string('a', 500) + "TONGCONG" + new string('b', 500);

        var clipped = WebSnapshot.ClipPageText(text, 400);

        Assert.StartsWith("aaa", clipped);
        Assert.EndsWith("bbb", clipped);
        Assert.Contains("cắt bớt", clipped);
    }

    [Fact]
    public void ClipPageText_LeavesShortTextAlone()
    {
        Assert.Equal("ngắn gọn", WebSnapshot.ClipPageText("ngắn gọn", 3000));
    }
}
