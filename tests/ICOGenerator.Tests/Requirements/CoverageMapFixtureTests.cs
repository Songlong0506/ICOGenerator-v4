using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Fixture của cả chục test khác dựng bản đồ bằng dạng bullet; nếu nó trôi khỏi format thật thì các test
// đó vẫn xanh trong khi kiểm sai thứ. Chốt bằng phép đi vòng: bullet → HAI cột → ToText phải ra lại y
// nguyên. Vòng này đi qua cả phép GẮN câu hỏi vào dòng, vì đó là thứ dựng lại vế "còn thiếu:".
public class CoverageMapFixtureTests
{
    [Theory]
    [InlineData("- ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.")]
    [InlineData("- ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn.")]
    [InlineData("- Vòng đời & trạng thái: [MỘT PHẦN] Đơn có 3 trạng thái. còn thiếu: ai chuyển trạng thái")]
    [InlineData("- Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Người dùng nói không cần.")]
    [InlineData("- Thông báo / nhắc nhở: [CHƯA HỎI]")]
    public void Fixture_RoundTripsThroughTheRealFormat(string bullet)
    {
        Assert.Equal(bullet, CoverageMapParser.ToText(CoverageMapParser.AttachQuestions(
            CoverageMapParser.Parse(CoverageMapFixture.Map(bullet)), CoverageMapFixture.Questions(bullet))));
    }

    [Fact]
    public void Fixture_SplitsKnownAndGap()
    {
        var item = Assert.Single(CoverageMapFixture.Items(
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Có 3 vai trò. | Nhân viên, quản lý, HR. "
            + "còn thiếu: mỗi vai trò làm được gì"));

        Assert.True(item.IsCore);
        Assert.Equal("Đối tượng người dùng & vai trò", item.Label);
        Assert.Equal("MỘT PHẦN", item.Status);
        Assert.Equal(new[] { "Có 3 vai trò.", "Nhân viên, quản lý, HR." }, item.Known);
        Assert.Equal("mỗi vai trò làm được gì", Assert.Single(item.Questions));
    }

    // Bản đồ dựng ra phải là JSON — nếu helper lỡ trả nguyên chuỗi bullet thì mọi test dùng nó sẽ kiểm
    // đúng cái đường mà production vừa bỏ đi.
    [Fact]
    public void Map_ProducesJson()
    {
        Assert.StartsWith("{", CoverageMapFixture.Map("- Mục tiêu: [RÕ] x"), StringComparison.Ordinal);
    }
}
