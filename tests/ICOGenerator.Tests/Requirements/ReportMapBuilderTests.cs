using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bảng BÁO CÁO / THỐNG KÊ — bảng BA ráp ngầm từ các báo cáo người dùng đã chọn (ReportMapDraftService), người
// dùng không rà nó. Bốn thứ phải giữ bằng test, và cả bốn đều là chỗ mà một lần sửa vô ý làm hỏng một tuyến
// chứ không chỉ một ô:
//
//  1. Ô "lấy số từ" là chữ TỰ DO: không còn bảng đối tượng nào để đối chiếu, nên chữ model viết phải được
//     giữ nguyên.
//  2. Mỗi dòng còn giữ đều CÓ CHỖ ở bảng màn hình: mặc định là một chức năng của MỘT màn tổng quan chung
//     (người dùng muốn một cái nhìn overview, không phải mỗi báo cáo một mục menu), và chỉ thành màn hình
//     riêng khi model khai có lý do (OwnScreen). ReportScreens/ReportScreenAdditions là đường bảng này chảy
//     vào bảng màn hình; hỏng nó là mọi báo cáo mất luôn dòng phân quyền và mục ở "## 6. Screens To Generate".
//  3. Mọi báo cáo đều bị bỏ (dữ liệu của dự án cũ) vẫn phải ra khối ngữ cảnh. "Ứng dụng không cần báo cáo
//     nào" là một QUYẾT ĐỊNH; trả null ở ca đó là để BA đề xuất lại đúng danh sách vừa bị tắt.
public class ReportMapBuilderTests
{
    private static ReportMapRow Row(string report, string source = "", bool included = true)
        => new() { Report = report, Question = "để biết ai chưa đi học", Source = source, Breakdown = "quý; đơn vị", Included = included };

    // ==== Ô "LẤY SỐ TỪ" ====

    [Fact]
    public void Build_KeepsTheSourceVerbatim()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo chi phí", "  Bảng lương ") });
        Assert.Equal("Bảng lương", rows.Single().Source);
    }

    // ==== CỜ GIỮ/BỎ ====

    // Mọi dòng ra khỏi Build ở trạng thái được giữ, bất kể model trả gì. Cờ `included` là chỗ NGƯỜI DÙNG nói
    // "báo cáo này không cần" (qua bảng màn hình) — structured output luôn phải điền đủ trường, nên một model
    // điền false cho có sẽ âm thầm loại sạch bảng, và không màn hình báo cáo nào được gieo.
    [Fact]
    public void Build_IgnoresIncludedFromTheModel()
    {
        var rows = ReportMapBuilder.Build(new[] { Row("Báo cáo tiến độ", included: false) });
        Assert.True(rows.Single().Included);
    }

    [Fact]
    public void Build_DropsNamelessAndDuplicateRows()
    {
        var rows = ReportMapBuilder.Build(
            new[] { Row(""), Row("Báo cáo tiến độ"), Row("  báo cáo tiến độ  ") });

        Assert.Single(rows);
    }

    // ==== GOM VỀ MÀN TỔNG QUAN ====

    private static List<ReportMapRow> LockerReports() => ReportMapBuilder.Build(new[]
    {
        new ReportMapRow { Report = "Locker Status Report", Question = "để biết khu nào còn tủ trống", Breakdown = "khu; trạng thái" },
        new ReportMapRow { Report = "Damaged Locker Report", Question = "để biết tủ nào chờ sửa", FilteredList = true, OwnScreen = true },
        new ReportMapRow { Report = "Department Cost", Question = "để nộp kế toán cuối tháng", OwnScreen = true }
    }, "Locker Dashboard");

    // Ca thật (dự án Locker): ba báo cáo ra ba màn hình, trong đó hai cái chỉ là danh sách của một màn làm
    // việc lọc theo trạng thái. Mặc định phải là MỘT màn tổng quan, mỗi báo cáo một chức năng bỏ tích được.
    [Fact]
    public void ReportScreenAdditions_GatherTheReportsOnOneDashboard_EachAsAFunction()
    {
        var additions = ReportMapBuilder.ReportScreenAdditions(LockerReports());

        var dashboard = additions[0];
        Assert.Equal("Locker Dashboard", dashboard.Screen);
        Assert.Equal(ReportMapBuilder.DashboardPurpose, dashboard.Purpose);
        Assert.Equal(2, dashboard.Functions.Count);
        Assert.StartsWith("Locker Status Report — để biết khu nào còn tủ trống", dashboard.Functions[0]);
        Assert.StartsWith("Damaged Locker Report", dashboard.Functions[1]);
    }

    // Màn riêng chỉ cho báo cáo model khai có lý do — và danh sách lọc thì KHÔNG BAO GIỜ, kể cả khi model
    // khai cả hai cờ: dựng nó thành màn hình là dựng lại danh sách mà màn làm việc đã có.
    [Fact]
    public void Build_GivesAnOwnScreenOnlyToReportsThatAskForIt_AndNeverToAFilteredList()
    {
        var rows = LockerReports();

        Assert.Equal("Locker Dashboard", rows[0].Screen);
        Assert.False(rows[1].OwnScreen);
        Assert.Equal("Locker Dashboard", rows[1].Screen);
        Assert.Equal("Department Cost Report", rows[2].Screen);
        Assert.Equal(new[] { "Locker Dashboard", "Department Cost Report" }, ReportMapBuilder.ReportScreens(rows));
    }

    [Theory]
    [InlineData(null, "Overview Dashboard")]
    [InlineData("  ", "Overview Dashboard")]
    [InlineData("Locker", "Locker Dashboard")]
    [InlineData("Training  Overview", "Training Overview")]
    public void DashboardName_FallsBackAndSuffixes(string? proposed, string expected)
        => Assert.Equal(expected, ReportMapBuilder.DashboardName(proposed));

    // ==== MÀN HÌNH RIÊNG (và dòng ráp từ hồi mỗi báo cáo còn là một màn hình: Screen rỗng) ====

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

    [Fact]
    public void ReportScreens_SkipsRowsTheUserDropped()
    {
        var screens = ReportMapBuilder.ReportScreens(new[] { Row("Báo cáo tiến độ", included: false) });
        Assert.Empty(screens);
    }

    // Ô "việc của màn" gieo sẵn bằng câu hỏi báo cáo trả lời: đó là thứ người dùng nhận ra khi rà bảng màn
    // hình, thay cho một dòng chỉ có tên.
    [Fact]
    public void ReportScreenAdditions_CarryTheQuestionAsThePurpose()
    {
        var addition = ReportMapBuilder.ReportScreenAdditions(new[] { Row("Remaining Leave") }).Single();

        Assert.Equal("Remaining Leave Report", addition.Screen);
        Assert.Equal("để biết ai chưa đi học", addition.Purpose);
    }

    // ==== KHỐI NGỮ CẢNH ====

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

    // Tầng sau (lượt bày bảng màn hình, prompt sinh spec) phải đọc ra được báo cáo nào là một PHẦN của màn
    // tổng quan, cái nào chỉ là ô đếm, cái nào là màn riêng — nếu không spec lại tách mỗi báo cáo một màn.
    [Fact]
    public void RenderConfirmedBlock_SaysWhereEachReportLives()
    {
        var block = ReportMapBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(LockerReports()))!;

        Assert.Contains("«Locker Dashboard» GOM", block);
        Assert.Contains("chỉ là Ô ĐẾM", block);
        Assert.Contains("màn hình: Department Cost Report", block);
    }

    // Bỏ SẠCH là một câu trả lời, không phải một bảng rỗng — xem ghi chú đầu file.
    [Fact]
    public void RenderConfirmedBlock_StillSpeaks_WhenTheUserDroppedEveryReport()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { Row("Báo cáo tiến độ", included: false) });

        var block = ReportMapBuilder.RenderConfirmedBlock(json)!;
        Assert.Contains("KHÔNG cần báo cáo/thống kê nào", block);
    }

    [Fact]
    public void RenderConfirmedBlock_IsNull_WhenThereIsNoTable()
    {
        Assert.Null(ReportMapBuilder.RenderConfirmedBlock(null));
        Assert.Null(ReportMapBuilder.RenderConfirmedBlock("[]"));
    }
}
