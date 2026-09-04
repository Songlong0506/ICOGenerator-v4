using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Chốt chặn chống MẤT TRẮNG phần đã ghi nhận của một dòng. Ranh giới của nó là thứ đáng chốt nhất: nó
// phải im lặng ở mọi phép sửa hợp lệ (xoá mẩu người dùng vừa đính chính, gộp hai mẩu làm một) và chỉ nói
// ở đúng trạng thái KHÔNG THỂ ĐÚNG — còn nhận là biết mà không còn giữ chữ nào.
public class CoverageKnownLossGuardTests
{
    [Fact]
    public void Apply_RestoresKnownWipedFromAClearRow()
    {
        var items = Items("- ★ Mục tiêu / bài toán: [RÕ]");
        var previous = Items("- ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học bắt buộc. | Mỗi khóa có thời hạn hiệu lực.");

        CoverageKnownLossGuard.Apply(items, previous);

        Assert.Equal(
            new[] { "App quản lý khóa học bắt buộc.", "Mỗi khóa có thời hạn hiệu lực." },
            items[0].Known);
    }

    // Đính chính là việc HỢP LỆ và là lý do `known` được phép xoá phần tử: người dùng nói A rồi sửa thành
    // C thì bản đồ chỉ còn C. Guard đếm số mẩu sẽ chặn đúng ca này, nên nó KHÔNG được đếm.
    [Fact]
    public void Apply_LeavesARowThatShrankButStillCarriesSomething()
    {
        var items = Items("- ★ Mục tiêu / bài toán: [RÕ] Khóa học có hiệu lực 2 năm.");
        var previous = Items("- ★ Mục tiêu / bài toán: [RÕ] Khóa học có hiệu lực 1 năm. | Nhắc học lại trước 1 tháng.");

        CoverageKnownLossGuard.Apply(items, previous);

        Assert.Equal(new[] { "Khóa học có hiệu lực 2 năm." }, items[0].Known);
    }

    // [CHƯA HỎI] rỗng là ĐÚNG định nghĩa — một dòng vừa bị "New Chat" hay vừa được người dùng tuyên bố
    // không áp dụng không được guard nhồi lại nội dung cũ.
    [Theory]
    [InlineData("CHƯA HỎI")]
    [InlineData("KHÔNG ÁP DỤNG")]
    public void Apply_LeavesRowsWhereEmptyIsTheRightAnswer(string status)
    {
        var items = Items($"- ★ Mục tiêu / bài toán: [{status}]");
        var previous = Items("- ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học.");

        CoverageKnownLossGuard.Apply(items, previous);

        Assert.Empty(items[0].Known);
    }

    // Khớp theo NHÃN: 12 dòng đúng thứ tự là luật cho model, không phải bảo đảm. So theo vị trí thì một
    // lượt trả về lệch thứ tự sẽ trả phần đã ghi nhận của nhóm này cho nhóm khác — sai còn tệ hơn rỗng.
    [Fact]
    public void Apply_MatchesByLabel_NotByPosition()
    {
        var items = Items("""
            - Báo cáo / thống kê: [MỘT PHẦN]
            - ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học.
            """);
        var previous = Items("""
            - ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học.
            - Báo cáo / thống kê: [MỘT PHẦN] Cần báo cáo tỷ lệ hoàn thành theo phòng ban.
            """);

        CoverageKnownLossGuard.Apply(items, previous);

        Assert.Equal(new[] { "Cần báo cáo tỷ lệ hoàn thành theo phòng ban." }, items[0].Known);
    }

    private static List<CoverageMapItem> Items(string bullets) => CoverageMapFixture.Items(bullets).ToList();
}
