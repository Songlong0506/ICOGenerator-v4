using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// PHẠM VI MÀN HÌNH TRÔI TIẾP SAU LÚC BẢNG ĐÃ CHỐT — và trước đây không có đường nào đưa phần trôi đó vào
// bảng.
//
// Ca thật (dự án Learning and Development 7): người dùng rà và chốt bảng màn hình ở lượt 23. Tới lượt 33
// họ nói sĩ số tối thiểu/tối đa lấy từ "danh sách khóa học được quản lý ở một màn hình riêng", và Admin
// thì đã được chốt từ lượt 25 là người quản lý cả phòng học lẫn người dạy. Ba màn hình đó vào
// PlannedScope nhưng không bao giờ đi qua bảng: EffectiveScreens bù chúng vào bảng phân quyền ở dạng
// TRẮNG — không việc, không chức năng, không bước luồng — trong khi khối ngữ cảnh của bảng đã chốt CẤM BA
// hỏi lại việc của từng màn. Chúng đi thẳng vào tài liệu và vào bản demo mà không ai biết chúng để làm gì.
//
// Hai nửa của chốt chặn, và nửa thứ hai quan trọng ngang nửa đầu: cổng phải mở LẠI được, nhưng lượt bày
// lại KHÔNG được xóa phần người dùng đã tự tay rà.
public class ScreenScopeDriftTests
{
    private const string CoverageWithMainFlowClear = """
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo. {nguồn: "lên kế hoạch các lớp học"}
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý. {nguồn: "Đúng luồng này"}
        """;

    private const string ConfirmedScreens = """
        [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
          "functions":[{"name":"Tạo version plan","flowSteps":["Tạo một version plan"],"included":true},
                       {"name":"Xóa version plan","flowSteps":[],"included":false}],
          "covers":["Tính năng Generate Training Implement từ Training Plan Detail"],"included":true},
         {"screen":"Trang Master List","purpose":"Upload file Excel","functions":[],"included":false}]
        """;

    private static Project ProjectWith(params string[] plannedScope) => new()
    {
        RequirementCoverageMap = CoverageWithMainFlowClear,
        ScreenScopeMap = ConfirmedScreens,
        PlannedScope = string.Join("\n", plannedScope.Select(s => "- " + s))
    };

    // Màn hình lộ ra SAU lúc chốt ⇒ bảng mở lại. Không có nó thì màn hình ấy chỉ còn một đường vào hệ
    // thống: một dòng trắng trong bảng phân quyền.
    [Fact]
    public void Gate_ReopensTheTable_WhenANewScreenShowsUpAfterConfirmation()
    {
        var project = ProjectWith("Trang Training Plan", "Trang danh sách khóa học");

        Assert.True(ScreenScopeGate.ShouldAsk(project));
        Assert.Equal(InterviewTableKind.ScreenScope, InterviewTableGate.Select(project));
    }

    // Không có gì mới ⇒ ĐÓNG. Bày lại một bảng y hệt là bắt người dùng làm lại việc vừa làm, đúng thứ mà
    // luật "không hỏi lại điều đã trả lời" cấm — và ở đây nó tốn trọn một lượt phỏng vấn.
    [Fact]
    public void Gate_StaysClosed_WhenNothingNewAppeared()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith("Trang Training Plan")));
    }

    // Mục người dùng đã BỎ TÍCH không bao giờ quay lại, kể cả khi lượt chắt lọc vẫn giữ nó trong
    // PlannedScope. Mở lại thứ họ vừa đóng là đúng lỗi mà bảng cột đã cấm một lần.
    [Fact]
    public void Gate_DoesNotReopenForAScreenTheUserRemoved()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith("Trang Training Plan", "Trang Master List")));
    }

    // Mục đã được GỘP vào một màn hình khác (khai ở Covers) cũng không phải màn hình mới — nếu không, cổng
    // mở lại vĩnh viễn vì lượt chắt lọc vẫn đều đặn nhả ra mục đã gộp.
    [Fact]
    public void Gate_DoesNotReopenForAnItemAlreadyMergedIntoAnotherScreen()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith(
            "Trang Training Plan", "Tính năng Generate Training Implement từ Training Plan Detail")));
    }

    // Hạt giống của lượt bày lại: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức năng CÒN TÍCH — vì
    // Build cố ý trả mọi dòng ở trạng thái TÍCH SẴN, nên đưa thứ đã bỏ tích vào là bật lại đúng cái họ
    // vừa tắt.
    [Fact]
    public void SeedRows_KeepsOnlyWhatTheUserKept()
    {
        var seed = ScreenScopeMapBuilder.SeedRows(ConfirmedScreens);

        var row = Assert.Single(seed);
        Assert.Equal("Trang Training Plan", row.Screen);
        Assert.Equal("Tạo version plan", Assert.Single(row.Functions).Name);
    }

    // Nửa thứ hai của chốt chặn: bày lại KHÔNG được là một lượt phá hoại. Build dựng bảng từ đề xuất TƯƠI
    // của model, nên hạt giống phải thắng — việc của màn, chức năng và ô "phục vụ bước nào" mà người dùng
    // đã duyệt giữ nguyên, còn phần model đoán lại chỉ được lấp vào màn hình MỚI.
    [Fact]
    public void Rebuild_KeepsTheReviewedRows_AndOnlyLetsTheModelFillTheNewScreen()
    {
        var freshFromModel = new List<ICOGenerator.Contracts.Requirements.ScreenScopeRow>
        {
            // Model đoán lại màn hình người dùng ĐÃ duyệt — phải bị bỏ qua.
            new()
            {
                Screen = "Trang Training Plan",
                Purpose = "Model đoán lại việc của màn này",
                Functions = new List<ICOGenerator.Contracts.Requirements.ScreenFunction>
                {
                    new() { Name = "Một chức năng model vừa nghĩ ra" }
                }
            },
            new()
            {
                Screen = "Trang danh sách khóa học",
                Purpose = "Quản lý khóa học, sĩ số tối thiểu và tối đa",
                Functions = new List<ICOGenerator.Contracts.Requirements.ScreenFunction>
                {
                    new() { Name = "Cập nhật sĩ số tối thiểu – tối đa" }
                }
            }
        };

        var rebuilt = ScreenScopeMapBuilder.Build(
            ScreenScopeMapBuilder.SeedRows(ConfirmedScreens).Concat(freshFromModel),
            new List<string> { "Trang Training Plan", "Trang danh sách khóa học" });

        var reviewed = rebuilt.Single(r => r.Screen == "Trang Training Plan");
        Assert.Equal("Lập kế hoạch cả năm", reviewed.Purpose);
        Assert.Equal("Tạo version plan", Assert.Single(reviewed.Functions).Name);

        var added = rebuilt.Single(r => r.Screen == "Trang danh sách khóa học");
        Assert.Equal("Quản lý khóa học, sĩ số tối thiểu và tối đa", added.Purpose);
        Assert.Equal("Cập nhật sĩ số tối thiểu – tối đa", Assert.Single(added.Functions).Name);
    }

    // Bảng chốt mà KHÔNG dòng nào được giữ là bảng hỏng, không phải "ứng dụng không có màn hình nào" —
    // cùng luật fail-open với EffectiveScreens. Mở lại một bảng dựng trên bản chốt hỏng chỉ làm người dùng
    // rà lại từ đầu.
    [Fact]
    public void BrokenConfirmedTable_ReportsNoNewScreens()
    {
        const string allRemoved = """
            [{"screen":"Trang Training Plan","purpose":"","functions":[],"included":false}]
            """;

        Assert.Empty(ScreenScopeMapBuilder.NewScreens(allRemoved, new List<string> { "Trang gì đó mới" }));
    }

    // Bảng chưa chốt ⇒ không có "sau lúc chốt" nào để so.
    [Fact]
    public void UnconfirmedTable_ReportsNoNewScreens()
    {
        Assert.Empty(ScreenScopeMapBuilder.NewScreens(null, new List<string> { "Trang Training Plan" }));
    }
}
