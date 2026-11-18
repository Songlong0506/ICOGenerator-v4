using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// PHẠM VI MÀN HÌNH TRÔI TIẾP SAU LÚC BẢNG ĐÃ CHỐT — và trước đây không có đường nào đưa phần trôi đó vào
// bảng.
//
// (Thời các ca dưới đây, bảng màn hình còn bày ra cho người dùng chốt; nay BA tự soạn và chốt nó —
// ScreenScopeDraftService — nhưng bài toán phạm vi trôi vẫn y nguyên: cổng phải mở LẠI được, và lượt soạn
// lại không được đụng phần đã chốt.)
//
// Ca thật (dự án Learning and Development 7): người dùng rà và chốt bảng màn hình ở lượt 23. Tới lượt 33
// họ nói sĩ số tối thiểu/tối đa lấy từ "danh sách khóa học được quản lý ở một màn hình riêng", và Admin
// thì đã được chốt từ lượt 25 là người quản lý cả phòng học lẫn người dạy. Ba màn hình đó vào phạm vi
// nhưng không bao giờ đi qua bảng: EffectiveScreens đưa chúng vào bảng phân quyền ở dạng TRẮNG — không
// việc, không chức năng, không bước luồng — trong khi khối ngữ cảnh của bảng đã chốt CẤM BA hỏi lại việc
// của từng màn. Chúng đi thẳng vào tài liệu và vào bản demo mà không ai biết chúng để làm gì.
//
// Nay phần trôi ấy nằm ngay trong bảng ở trạng thái CHƯA CHỐT (ConfirmedByUser = false), nên cổng không
// phải so hai danh sách với nhau nữa — nó chỉ hỏi "còn mục nào chưa chốt không". Hai nửa của chốt chặn,
// và nửa thứ hai quan trọng ngang nửa đầu: cổng phải mở LẠI được, nhưng lượt soạn lại KHÔNG được xóa phần
// đã chốt.
public class ScreenScopeDriftTests
{
    private static readonly string CoverageWithMainFlowClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý.
        """);

    // Bảng ĐÃ CHỐT TRỌN: một màn hình được giữ (kèm một chức năng bị bỏ tích), một màn hình bị loại, và một
    // mục khai gộp. Mọi thứ đều mang dấu ConfirmedByUser.
    private const string ConfirmedScreens = """
        [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
          "functions":[{"name":"Tạo version plan","flowSteps":["Tạo một version plan"],"included":true,"confirmedByUser":true},
                       {"name":"Xóa version plan","flowSteps":[],"included":false,"confirmedByUser":true}],
          "covers":["Tính năng Generate Training Implement từ Training Plan Detail"],
          "included":true,"confirmedByUser":true},
         {"screen":"Trang Master List","purpose":"Upload file Excel","functions":[],
          "included":false,"confirmedByUser":true}]
        """;

    // Cùng bảng đó sau khi lượt chắt lọc ghép thêm một màn hình vừa lộ ra ở lượt 33.
    private const string ConfirmedScreensPlusNewOne = """
        [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
          "functions":[{"name":"Tạo version plan","flowSteps":["Tạo một version plan"],"included":true,"confirmedByUser":true},
                       {"name":"Xóa version plan","flowSteps":[],"included":false,"confirmedByUser":true}],
          "covers":["Tính năng Generate Training Implement từ Training Plan Detail"],
          "included":true,"confirmedByUser":true},
         {"screen":"Trang Master List","purpose":"Upload file Excel","functions":[],
          "included":false,"confirmedByUser":true},
         {"screen":"Trang danh sách khóa học","purpose":"","functions":[],
          "included":true,"confirmedByUser":false}]
        """;

    private static Project ProjectWith(string? screenScopeJson) => new()
    {
        RequirementCoverageMap = CoverageWithMainFlowClear,
        ScreenScopeMap = screenScopeJson
    };

    // Màn hình lộ ra SAU lúc chốt ⇒ cổng soạn mở lại. Không có nó thì màn hình ấy chỉ còn một đường vào hệ
    // thống: một dòng trắng trong bảng phân quyền. Và KHÔNG bảng nào được bày ra cho người dùng vì nó: phạm
    // vi màn hình là thiết kế của BA.
    [Fact]
    public void Gate_ReopensTheDraft_WhenANewScreenShowsUpAfterConfirmation()
    {
        var project = ProjectWith(ConfirmedScreensPlusNewOne);

        Assert.True(ScreenScopeGate.ShouldDraft(project));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(project));
    }

    // Phần trôi không chỉ là màn hình mới: một CHỨC NĂNG lộ ra trên màn hình đã chốt cũng phải được soạn
    // (ca gốc — dự án Locker: người dùng thêm "FCM bấm Đã clean"). Bản cũ chỉ so TÊN MÀN HÌNH nên cả màn
    // hình ấy vẫn "đã biết" và ca này đi thẳng vào tài liệu.
    [Fact]
    public void Gate_ReopensTheDraft_ForANewFunctionOnAConfirmedScreen()
    {
        const string withNewFunction = """
            [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
              "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true,"confirmedByUser":true},
                           {"name":"Xuất kế hoạch ra Excel","flowSteps":[],"included":true,"confirmedByUser":false}],
              "included":true,"confirmedByUser":true}]
            """;

        Assert.True(ScreenScopeGate.ShouldDraft(ProjectWith(withNewFunction)));
    }

    // Không có gì mới ⇒ ĐÓNG. Soạn lại một bảng y hệt là một lời gọi LLM cho không.
    [Fact]
    public void Gate_StaysClosed_WhenNothingNewAppeared()
    {
        Assert.False(ScreenScopeGate.ShouldDraft(ProjectWith(ConfirmedScreens)));
    }

    // Chưa có dòng nào ⇒ cũng ĐÓNG: bảng không có gì để soạn. Cùng một điều kiện chở cả hai ca.
    [Fact]
    public void Gate_StaysClosed_WhenTheTableIsEmpty()
    {
        Assert.False(ScreenScopeGate.ShouldDraft(ProjectWith(null)));
    }

    // Dòng đã BỎ TÍCH (dự án cũ) ở lại bảng làm BIA và KHÔNG mở lại cổng: mở lại thứ đã đóng là đúng lỗi
    // mà bảng cột đã cấm một lần.
    [Fact]
    public void Gate_DoesNotReopenForAScreenTheUserRemoved()
    {
        Assert.False(ScreenScopeGate.ShouldDraft(ProjectWith(ConfirmedScreens)));
        Assert.DoesNotContain("Trang Master List", ScreenScopeMapBuilder.EffectiveScreens(ConfirmedScreens));
    }

    // Hạt giống của lượt soạn lại: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức năng CÒN TÍCH — vì
    // Build cố ý trả mọi dòng ở trạng thái TÍCH SẴN, nên đưa thứ đã bỏ tích vào là bật lại đúng cái đã
    // tắt. Phần bị lọc ra không mất: Settle giữ nó lại lúc lưu.
    [Fact]
    public void SeedRows_KeepsOnlyWhatTheUserKept()
    {
        var seed = ScreenScopeMapBuilder.SeedRows(ConfirmedScreens);

        var row = Assert.Single(seed);
        Assert.Equal("Trang Training Plan", row.Screen);
        Assert.Equal("Tạo version plan", Assert.Single(row.Functions).Name);
    }

    // Nửa thứ hai của chốt chặn: soạn lại KHÔNG được là một lượt phá hoại. Build dựng bảng từ đề xuất TƯƠI
    // của model, nên dòng ĐÃ CHỐT phải thắng tuyệt đối — việc của màn, chức năng và ô "phục vụ bước nào"
    // giữ nguyên, vì bảng phân quyền đứng trên chúng — còn model chỉ được lấp vào dòng CHƯA CHỐT.
    [Fact]
    public void Rebuild_KeepsTheReviewedRows_AndOnlyLetsTheModelFillTheNewScreen()
    {
        var freshFromModel = new List<ScreenScopeRow>
        {
            // Model đoán lại màn hình ĐÃ chốt — phải bị bỏ qua.
            new()
            {
                Screen = "Trang Training Plan",
                Purpose = "Model đoán lại việc của màn này",
                Functions = new List<ScreenFunction> { new() { Name = "Một chức năng model vừa nghĩ ra" } }
            },
            new()
            {
                Screen = "Trang danh sách khóa học",
                Purpose = "Quản lý khóa học, sĩ số tối thiểu và tối đa",
                Functions = new List<ScreenFunction> { new() { Name = "Cập nhật sĩ số tối thiểu – tối đa" } }
            }
        };

        var rebuilt = ScreenScopeMapBuilder.Build(
            ScreenScopeMapBuilder.SeedRows(ConfirmedScreensPlusNewOne),
            freshFromModel,
            ScreenScopeMapBuilder.EffectiveScreens(ConfirmedScreensPlusNewOne));

        var reviewed = rebuilt.Single(r => r.Screen == "Trang Training Plan");
        Assert.Equal("Lập kế hoạch cả năm", reviewed.Purpose);
        Assert.Equal("Tạo version plan", Assert.Single(reviewed.Functions).Name);
        Assert.True(reviewed.ConfirmedByUser);

        var added = rebuilt.Single(r => r.Screen == "Trang danh sách khóa học");
        Assert.Equal("Quản lý khóa học, sĩ số tối thiểu và tối đa", added.Purpose);
        Assert.Equal("Cập nhật sĩ số tối thiểu – tối đa", Assert.Single(added.Functions).Name);
        Assert.False(added.ConfirmedByUser);
    }

    // Model KHÔNG được tự đóng dấu chốt: structured output buộc nó điền đủ trường, nên một
    // `confirmedByUser: true` điền cho có sẽ đóng cổng trước khi mục đó được mô tả và xếp bước luồng.
    [Fact]
    public void Rebuild_NeverLetsTheModelStampTheConfirmedFlag()
    {
        var rebuilt = ScreenScopeMapBuilder.Build(
            null,
            new List<ScreenScopeRow>
            {
                new()
                {
                    Screen = "Trang danh sách khóa học",
                    ConfirmedByUser = true,
                    Functions = new List<ScreenFunction> { new() { Name = "Xem danh sách", ConfirmedByUser = true } }
                }
            },
            new List<string> { "Trang danh sách khóa học" });

        var row = Assert.Single(rebuilt);
        Assert.False(row.ConfirmedByUser);
        Assert.False(Assert.Single(row.Functions).ConfirmedByUser);
    }

    // Lưu sau lượt soạn: MỌI dòng và MỌI chức năng mang dấu chốt (cổng đóng ngay), BIA của bảng đang lưu
    // được giữ (nếu không lượt chắt lọc kế tiếp dựng lại thứ đã loại), còn mục chưa chốt mà lượt soạn đã khai
    // GỘP vào một dòng khác thì bị bỏ — giữ nó lại ở trạng thái chưa chốt là để cổng mở lại mãi.
    [Fact]
    public void Settle_StampsEverything_KeepsTheTombstones_AndDropsWhatWasMerged()
    {
        const string stored = """
            [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
              "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true,"confirmedByUser":true},
                           {"name":"Xóa version plan","flowSteps":[],"included":false,"confirmedByUser":true}],
              "covers":[],"included":true,"confirmedByUser":true},
             {"screen":"Trang Master List","purpose":"","functions":[],"included":false,"confirmedByUser":true},
             {"screen":"Tính năng xuất Excel từ Training Plan","purpose":"","functions":[],"included":true,"confirmedByUser":false}]
            """;
        var drafted = ScreenScopeMapBuilder.Build(
            ScreenScopeMapBuilder.SeedRows(stored),
            new List<ScreenScopeRow>
            {
                new()
                {
                    Screen = "Trang Training Plan",
                    Covers = new List<string> { "Tính năng xuất Excel từ Training Plan" },
                    Functions = new List<ScreenFunction> { new() { Name = "Xuất Excel" } }
                }
            },
            ScreenScopeMapBuilder.EffectiveScreens(stored));

        var saved = ScreenScopeMapBuilder.Settle(stored, drafted);

        Assert.All(saved.Where(r => r.Included), r => Assert.True(r.ConfirmedByUser));
        Assert.All(saved.SelectMany(r => r.Functions).Where(f => f.Included), f => Assert.True(f.ConfirmedByUser));
        Assert.Contains(saved, r => r.Screen == "Trang Master List" && !r.Included);
        Assert.Contains(saved.Single(r => r.Screen == "Trang Training Plan").Functions, f => f.Name == "Xóa version plan" && !f.Included);
        Assert.DoesNotContain(saved, r => r.Screen == "Tính năng xuất Excel từ Training Plan");
        Assert.False(ScreenScopeMapBuilder.HasPending(System.Text.Json.JsonSerializer.Serialize(saved)));
    }
}
