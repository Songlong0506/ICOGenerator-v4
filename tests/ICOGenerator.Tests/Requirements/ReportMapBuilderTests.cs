using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bảng BÁO CÁO / THỐNG KÊ — bảng thứ ba của buổi phỏng vấn. Ba thứ phải giữ bằng test, và cả ba đều là chỗ
// mà một lần sửa vô ý làm hỏng một tuyến chứ không chỉ một ô:
//
//  1. Ô "lấy số từ" phải NỐI được về bảng đối tượng. Đây là mối nối duy nhất giữ cho bước sinh spec khỏi tự
//     nghĩ ra một nguồn dữ liệu cho báo cáo. Nguồn không khớp thì XOÁ ô chứ không loại dòng — một cái tên
//     nguồn sai không làm cả báo cáo trở nên sai, và người dùng còn thấy dòng để tự sửa.
//  2. Mỗi dòng còn tích là một MÀN HÌNH. ReportScreens là đường bảng này chảy vào PlannedScope; hỏng nó là
//     mọi báo cáo vừa chốt mất luôn dòng phân quyền và mục ở "## 6. Screens To Generate".
//  3. Bỏ tích SẠCH vẫn phải ra khối "đã chốt". "Ứng dụng không cần báo cáo nào" là một QUYẾT ĐỊNH; trả null
//     ở ca đó là để BA đề xuất lại đúng danh sách người dùng vừa tắt.
public class ReportMapBuilderTests
{
    private static readonly List<string> Entities = new() { "Kế hoạch đào tạo", "Đơn đăng ký" };

    private static ReportMapRow Row(string report, string source = "", bool included = true, bool added = false)
        => new() { Report = report, Question = "để biết ai chưa đi học", Source = source, Breakdown = "quý; đơn vị", Included = included, AddedByUser = added };

    // ==== Ô "LẤY SỐ TỪ" ====

    [Fact]
    public void Build_KeepsASourceThatNamesAConfirmedEntity()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo tiến độ đào tạo", "Kế hoạch đào tạo") }, Entities);
        Assert.Equal("Kế hoạch đào tạo", rows.Single().Source);
    }

    // Model gọi dài hơn tên của bảng đối tượng ("Đơn đăng ký khóa học") không phải một nguồn bịa — chuẩn hoá
    // về đúng tên của bảng kia để hai bảng còn nối được với nhau ở bước sinh spec.
    [Fact]
    public void Build_NormalizesALongerSpellingBackToTheEntityName()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo đăng ký", "Đơn đăng ký khóa học") }, Entities);
        Assert.Equal("Đơn đăng ký", rows.Single().Source);
    }

    [Fact]
    public void Build_ClearsAnInventedSource_ButKeepsTheRow()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo chi phí", "Bảng lương") }, Entities);
        Assert.Equal("Báo cáo chi phí", rows.Single().Report);
        Assert.Equal(string.Empty, rows.Single().Source);
    }

    // Chưa chốt bảng đối tượng ⇒ không có gì để đối chiếu. Giữ chữ của model còn hơn xoá sạch một cột mà
    // người dùng sẽ thấy trống trơn ở mọi dòng.
    [Fact]
    public void Build_KeepsTheSourceVerbatim_WhenThereIsNoEntityListToMatchAgainst()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo chi phí", "Bảng lương") }, new List<string>());
        Assert.Equal("Bảng lương", rows.Single().Source);
    }

    // ==== CỜ GIỮ/BỎ ====

    // Lượt BÀY BẢNG: mọi dòng ra ở trạng thái được giữ, bất kể model trả gì. Cờ `included` là chỗ NGƯỜI DÙNG
    // nói "báo cáo này không cần" — structured output luôn phải điền đủ trường, nên một model điền false cho
    // có sẽ âm thầm loại sạch bảng và người dùng gửi đi một bảng rỗng nghĩ rằng mình vừa xác nhận cả danh sách.
    [Fact]
    public void Build_IgnoresIncludedFromTheModel()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo tiến độ", included: false) }, Entities);
        Assert.True(rows.Single().Included);
    }

    // Lượt GỬI: giữ đúng thứ người dùng đã chọn, và dòng bị bỏ vẫn nằm lại payload để tin nhắn kể lại gọi
    // tên được nó.
    [Fact]
    public void Sanitize_RespectsIncludedAndKeepsDroppedRows()
    {
        var rows = ReportMapBuilder.Sanitize(
            new[] { Row("Báo cáo tiến độ", included: false), Row("Báo cáo chi phí") }, Entities);

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Included);
    }

    // Cờ "người dùng tự thêm" chỉ được đọc ở đường GỬI: ở lượt bày bảng mọi dòng đều do model soạn, nên đọc
    // nó ở đó là dựng cho model một cửa sau đi vòng chốt chặn bằng cách tự khai là người dùng.
    [Fact]
    public void Build_DropsTheAddedByUserFlagFromTheModel_ButSanitizeKeepsIt()
    {
        Assert.False(ReportMapBuilder.Build(new[] { Row("Báo cáo tự thêm", added: true) }, Entities).Single().AddedByUser);
        Assert.True(ReportMapBuilder.Sanitize(new[] { Row("Báo cáo tự thêm", added: true) }, Entities).Single().AddedByUser);
    }

    [Fact]
    public void Build_DropsNamelessAndDuplicateRows()
    {
        var rows = ReportMapBuilder.Build(
            new[] { Row(""), Row("Báo cáo tiến độ"), Row("  báo cáo tiến độ  ") }, Entities);

        Assert.Single(rows);
    }

    // ==== MÀN HÌNH GIEO RA ====

    // Tên đã tự đọc được như một màn hình thì giữ nguyên; tên trần mới được gắn hậu tố. Gắn cho tất cả thì
    // sinh ra những mục phạm vi đọc như "Headcount Report Report".
    [Fact]
    public void ReportScreens_SuffixesOnlyTheNamesThatDoNotAlreadyReadAsAScreen()
    {
        var screens = ReportMapBuilder.ReportScreens(new[]
        {
            Row("Headcount Report"),
            Row("Training Cost Dashboard"),
            Row("Remaining Leave")
        });

        Assert.Equal(
            new[] { "Headcount Report", "Training Cost Dashboard", "Remaining Leave Report" },
            screens);
    }

    // Dự án chốt bảng TRƯỚC khi phạm vi màn hình chuyển sang tiếng Anh vẫn còn tên tiếng Việt trong
    // ReportMap đã lưu — gắn hậu tố cho chúng thì ra "Báo cáo tổng hợp ngày phép Report".
    [Fact]
    public void ReportScreens_LeavesLegacyVietnameseNamesAlone()
    {
        var screens = ReportMapBuilder.ReportScreens(new[]
        {
            Row("Báo cáo tổng hợp ngày phép"),
            Row("Thống kê chi phí đào tạo")
        });

        Assert.Equal(new[] { "Báo cáo tổng hợp ngày phép", "Thống kê chi phí đào tạo" }, screens);
    }

    [Fact]
    public void ReportScreens_SkipsRowsTheUserDropped()
    {
        var screens = ReportMapBuilder.ReportScreens(new[] { Row("Báo cáo tiến độ", included: false) });
        Assert.Empty(screens);
    }

    // ==== KHỐI NGỮ CẢNH + TIN NHẮN KỂ LẠI ====

    [Fact]
    public void RenderConfirmedBlock_NamesTheDroppedReports_SoTheyAreNotProposedAgain()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            Row("Báo cáo tiến độ", "Kế hoạch đào tạo"),
            Row("Báo cáo chi phí", included: false)
        });

        var block = ReportMapBuilder.RenderConfirmedBlock(json)!;
        Assert.Contains("Báo cáo tiến độ", block);
        Assert.Contains("lấy số từ: Kế hoạch đào tạo", block);
        Assert.Contains("KHÔNG cần các báo cáo sau", block);
        Assert.Contains("Báo cáo chi phí", block);
    }

    // Bỏ tích SẠCH là một câu trả lời, không phải một bảng rỗng — xem ghi chú đầu file.
    [Fact]
    public void RenderConfirmedBlock_StillSpeaks_WhenTheUserDroppedEveryReport()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { Row("Báo cáo tiến độ", included: false) });

        var block = ReportMapBuilder.RenderConfirmedBlock(json)!;
        Assert.Contains("KHÔNG cần báo cáo/thống kê nào", block);
    }

    [Fact]
    public void RenderConfirmedBlock_IsNull_WhenNothingWasConfirmed()
    {
        Assert.Null(ReportMapBuilder.RenderConfirmedBlock(null));
        Assert.Null(ReportMapBuilder.RenderConfirmedBlock("[]"));
    }

    [Fact]
    public void RenderUserMessage_SaysSoWhenNoReportIsNeeded()
    {
        var message = ReportMapBuilder.RenderUserMessage(new[] { Row("Báo cáo tiến độ", included: false) });
        Assert.Contains("không cần báo cáo", message);
        Assert.Contains("(bỏ: Báo cáo tiến độ)", message);
    }
}
