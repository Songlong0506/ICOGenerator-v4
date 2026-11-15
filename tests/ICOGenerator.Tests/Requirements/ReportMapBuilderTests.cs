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
//  2. Mỗi dòng còn giữ là một MÀN HÌNH. ReportScreens/ReportScreenAdditions là đường bảng này chảy vào bảng
//     màn hình; hỏng nó là mọi báo cáo mất luôn dòng phân quyền và mục ở "## 6. Screens To Generate".
//  3. Bảng màn hình là chỗ DUY NHẤT người dùng bỏ được một báo cáo: bỏ tích màn hình của nó phải kéo báo cáo
//     ra theo (DropUnscreened), nếu không khối ngữ cảnh vẫn kể một báo cáo mà phạm vi đã loại.
//  4. Mọi báo cáo đều bị bỏ vẫn phải ra khối ngữ cảnh. "Ứng dụng không cần báo cáo nào" là một QUYẾT ĐỊNH;
//     trả null ở ca đó là để BA đề xuất lại đúng danh sách người dùng vừa tắt.
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

    // ==== BỎ BÁO CÁO QUA BẢNG MÀN HÌNH ====

    [Fact]
    public void DropUnscreened_DropsTheReportWhoseScreenTheUserUnticked()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { Row("Remaining Leave"), Row("Headcount Report") });
        var screens = new[]
        {
            new ScreenScopeRow { Screen = "Remaining Leave Report", Included = false },
            new ScreenScopeRow { Screen = "Headcount Report", Included = true }
        };

        var rows = ReportMapBuilder.Parse(ReportMapBuilder.DropUnscreened(json, screens));

        Assert.False(rows.Single(r => r.Report == "Remaining Leave").Included);
        Assert.True(rows.Single(r => r.Report == "Headcount Report").Included);
    }

    // Không có gì đổi ⇒ null, để use case không ghi lại cột khi người dùng không bỏ báo cáo nào.
    [Fact]
    public void DropUnscreened_IsNull_WhenNoReportScreenWasDropped()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { Row("Headcount Report") });

        Assert.Null(ReportMapBuilder.DropUnscreened(json, new[]
        {
            new ScreenScopeRow { Screen = "Headcount Report", Included = true },
            new ScreenScopeRow { Screen = "Class List", Included = false }
        }));
        Assert.Null(ReportMapBuilder.DropUnscreened(null, Array.Empty<ScreenScopeRow>()));
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
