using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// PHẠM VI MÀN HÌNH TRÔI TIẾP SAU LÚC BẢNG ĐÃ CHỐT — và trước đây không có đường nào đưa phần trôi đó vào
// bảng.
//
// Ca thật (dự án Learning and Development 7): người dùng rà và chốt bảng màn hình ở lượt 23. Tới lượt 33
// họ nói sĩ số tối thiểu/tối đa lấy từ "danh sách khóa học được quản lý ở một màn hình riêng", và Admin
// thì đã được chốt từ lượt 25 là người quản lý cả phòng học lẫn người dạy. Ba màn hình đó vào phạm vi
// nhưng không bao giờ đi qua bảng: EffectiveScreens đưa chúng vào bảng phân quyền ở dạng TRẮNG — không
// việc, không chức năng, không bước luồng — trong khi khối ngữ cảnh của bảng đã chốt CẤM BA hỏi lại việc
// của từng màn. Chúng đi thẳng vào tài liệu và vào bản demo mà không ai biết chúng để làm gì.
//
// Nay phần trôi ấy nằm ngay trong bảng ở trạng thái CHỜ DUYỆT (ConfirmedByUser = false), nên cổng không
// phải so hai danh sách với nhau nữa — nó chỉ hỏi "còn mục nào chưa ai rà không". Hai nửa của chốt chặn,
// và nửa thứ hai quan trọng ngang nửa đầu: cổng phải mở LẠI được, nhưng lượt bày lại KHÔNG được xóa phần
// người dùng đã tự tay rà.
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

    // Màn hình lộ ra SAU lúc chốt ⇒ bảng mở lại. Không có nó thì màn hình ấy chỉ còn một đường vào hệ
    // thống: một dòng trắng trong bảng phân quyền.
    [Fact]
    public void Gate_ReopensTheTable_WhenANewScreenShowsUpAfterConfirmation()
    {
        var project = ProjectWith(ConfirmedScreensPlusNewOne);

        Assert.True(ScreenScopeGate.ShouldAsk(project));
        Assert.Equal(InterviewTableKind.ScreenScope, InterviewTableGate.Select(project));
    }

    // Phần trôi không chỉ là màn hình mới: một CHỨC NĂNG lộ ra trên màn hình đã chốt cũng phải được rà.
    // Bản cũ chỉ so TÊN MÀN HÌNH nên cả màn hình ấy vẫn "đã biết" và ca này đi thẳng vào tài liệu.
    [Fact]
    public void Gate_ReopensTheTable_ForANewFunctionOnAConfirmedScreen()
    {
        const string withNewFunction = """
            [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
              "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true,"confirmedByUser":true},
                           {"name":"Xuất kế hoạch ra Excel","flowSteps":[],"included":true,"confirmedByUser":false}],
              "included":true,"confirmedByUser":true}]
            """;

        Assert.True(ScreenScopeGate.ShouldAsk(ProjectWith(withNewFunction)));
    }

    // Không có gì mới ⇒ ĐÓNG. Bày lại một bảng y hệt là bắt người dùng làm lại việc vừa làm, đúng thứ mà
    // luật "không hỏi lại điều đã trả lời" cấm — và ở đây nó tốn trọn một lượt phỏng vấn.
    [Fact]
    public void Gate_StaysClosed_WhenNothingNewAppeared()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith(ConfirmedScreens)));
    }

    // Chưa có dòng nào ⇒ cũng ĐÓNG: bảng không có gì để hỏi. Cùng một điều kiện chở cả hai ca.
    [Fact]
    public void Gate_StaysClosed_WhenTheTableIsEmpty()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith(null)));
    }

    // Dòng người dùng đã BỎ TÍCH ở lại bảng làm BIA và KHÔNG mở lại cổng: mở lại thứ họ vừa đóng là đúng
    // lỗi mà bảng cột đã cấm một lần.
    [Fact]
    public void Gate_DoesNotReopenForAScreenTheUserRemoved()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(ProjectWith(ConfirmedScreens)));
        Assert.DoesNotContain("Trang Master List", ScreenScopeMapBuilder.EffectiveScreens(ConfirmedScreens));
    }

    // Hạt giống của lượt bày lại: chỉ màn hình CÒN TÍCH, và trong mỗi màn chỉ chức năng CÒN TÍCH — vì
    // Build cố ý trả mọi dòng ở trạng thái TÍCH SẴN, nên đưa thứ đã bỏ tích vào là bật lại đúng cái họ
    // vừa tắt. Phần bị lọc ra không mất: MergeConfirmed giữ nó lại lúc lưu.
    [Fact]
    public void SeedRows_KeepsOnlyWhatTheUserKept()
    {
        var seed = ScreenScopeMapBuilder.SeedRows(ConfirmedScreens);

        var row = Assert.Single(seed);
        Assert.Equal("Trang Training Plan", row.Screen);
        Assert.Equal("Tạo version plan", Assert.Single(row.Functions).Name);
    }

    // Nửa thứ hai của chốt chặn: bày lại KHÔNG được là một lượt phá hoại. Build dựng bảng từ đề xuất TƯƠI
    // của model, nên dòng ĐÃ CHỐT phải thắng tuyệt đối — việc của màn, chức năng và ô "phục vụ bước nào"
    // giữ nguyên — còn model chỉ được lấp vào dòng CHƯA AI RÀ.
    [Fact]
    public void Rebuild_KeepsTheReviewedRows_AndOnlyLetsTheModelFillTheNewScreen()
    {
        var freshFromModel = new List<ScreenScopeRow>
        {
            // Model đoán lại màn hình người dùng ĐÃ duyệt — phải bị bỏ qua.
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

    // Model KHÔNG được tự đóng dấu chữ ký người dùng: structured output buộc nó điền đủ trường, nên một
    // `confirmedByUser: true` điền cho có sẽ khai tử cả cổng — bảng hiện ra và không còn mục nào chờ duyệt.
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

    // NỬA THỨ TƯ, và nó ở tầng cuối cùng: bảng bày lại phải SỐNG SÓT QUA F5. Trang Requirements dựng lại
    // panel từ lượt hội thoại, nhưng điều kiện cũ là "Project.ScreenScopeMap còn null" — đúng cho ba bảng
    // chốt-một-lần kia, sai cho đúng bảng này vì ở lượt bày lại cột đó đã mang dấu chốt từ lần trước. Ca
    // thật: BA bày bảng bổ sung 8 màn hình, người dùng F5 rồi bảng biến mất, và không còn đường nào để gửi
    // — các màn hình mới quay lại đúng chỗ cũ: một dòng TRẮNG trong bảng phân quyền.
    [Fact]
    public void PendingRows_KeepsTheReshownTable_AfterARefresh()
    {
        const string reshownTurn = """
            [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
              "functions":[{"name":"Tạo version plan","flowSteps":["Tạo một version plan"],"included":true}],
              "covers":["Tính năng Generate Training Implement từ Training Plan Detail"],"included":true},
             {"screen":"Trang danh sách khóa học","purpose":"Quản lý khóa học","functions":[],"included":true}]
            """;

        var pending = ScreenScopeMapBuilder.PendingRows(ConfirmedScreens, reshownTurn);

        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, r => r.Screen == "Trang danh sách khóa học");
    }

    // Vòng lặp có đáy: gửi xong thì mọi màn hình VÀ mọi chức năng của bảng vừa bày đều mang dấu trong bản
    // chốt — kể cả dòng người dùng BỎ TÍCH và mục khai gộp — nên panel tự đóng. Không có đáy này thì bảng ở
    // lì trên màn hình sau khi đã được trả lời, đúng thứ luật "không hỏi lại điều đã trả lời" cấm.
    [Fact]
    public void PendingRows_ClosesThePanel_OnceThatTableHasBeenSubmitted()
    {
        const string renderedTurn = """
            [{"screen":"Trang Training Plan","purpose":"",
              "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true}],"included":true},
             {"screen":"Trang Master List","purpose":"","functions":[],"included":true}]
            """;

        Assert.Empty(ScreenScopeMapBuilder.PendingRows(ConfirmedScreens, renderedTurn));
    }

    // Chức năng mới trên một màn hình đã chốt cũng giữ panel lại: bảng bày ra vì nó, mà F5 làm nó biến mất
    // thì chức năng ấy đi vào tài liệu không ai rà — đúng lỗ hổng của phép so chỉ nhìn tên màn hình.
    [Fact]
    public void PendingRows_KeepsTheTable_ForANewFunctionOnAConfirmedScreen()
    {
        const string renderedTurn = """
            [{"screen":"Trang Training Plan","purpose":"Lập kế hoạch cả năm",
              "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true},
                           {"name":"Xuất kế hoạch ra Excel","flowSteps":[],"included":true}],"included":true}]
            """;

        Assert.Single(ScreenScopeMapBuilder.PendingRows(ConfirmedScreens, renderedTurn));
    }

    // Lần bày ĐẦU giữ nguyên hành vi cũ: chưa chốt gì thì bảng treo tới lúc được gửi.
    [Fact]
    public void PendingRows_KeepsTheFirstTable_WhileNothingIsConfirmedYet()
    {
        const string firstTurn = """
            [{"screen":"Trang Training Plan","purpose":"","functions":[],"included":true}]
            """;

        Assert.Single(ScreenScopeMapBuilder.PendingRows(null, firstTurn));
        Assert.Empty(ScreenScopeMapBuilder.PendingRows(ConfirmedScreens, null));
    }

    // NỬA THỨ BA của chốt chặn, và nó nằm ở chỗ người dùng thật sự nhìn: câu dẫn. Cơ chế đã làm đúng —
    // SeedRows giữ nguyên phần đã rà, cờ chờ duyệt biết chính xác cái gì mới — nhưng nếu câu dẫn vẫn là lời
    // mời rà bảng như lần đầu thì với người dùng, một bảng màn hình hiện ra lần thứ hai đọc lên là "BA quên
    // mình vừa gửi bảng này rồi". Ca thật (JD Libary 1, lượt 22): model tự viết "anh/chị rà soát bảng màn
    // hình dưới đây rồi bấm Gửi bảng màn hình" — không một chữ nào nói phần cũ được giữ hay màn hình nào
    // mới.
    [Fact]
    public void ReshowIntro_NamesTheNewScreens_AndSaysTheConfirmedPartIsKept()
    {
        var intro = BAChatService.ScreenScopeReshowIntro(
            new List<string> { "Trang danh sách khóa học" }, new List<string>());

        Assert.Contains("giữ nguyên", intro);
        Assert.Contains("Trang danh sách khóa học", intro);
        Assert.Contains("một màn hình", intro);
        // Lượt có bảng không có chip, nên câu dẫn phải CHỈ VÀO nút gửi — cùng luật với bốn câu dẫn kia.
        Assert.Contains("Gửi bảng màn hình", intro);
    }

    // Có chức năng mới thì câu dẫn phải đổi cách gọi: người dùng nghe "còn 2 màn hình nữa" rồi mở ra thấy
    // đúng bảng cũ với một dòng con lạ sẽ đi tìm cái màn hình không có thật.
    [Fact]
    public void ReshowIntro_CallsThemItems_WhenANewFunctionIsInvolved()
    {
        var intro = BAChatService.ScreenScopeReshowIntro(
            new List<string>(), new List<string> { "Xuất kế hoạch ra Excel (ở Trang Training Plan)" });

        Assert.Contains("một mục", intro);
        Assert.DoesNotContain("một màn hình", intro);
        Assert.Contains("Xuất kế hoạch ra Excel", intro);
    }

    // Danh sách dài thì gọi tên vài mục rồi gộp phần dư: một câu dẫn liệt kê 12 tên không ai đọc hết, mà
    // con số tổng mới là thứ nói cho người dùng biết lượt này tốn bao nhiêu công.
    [Fact]
    public void ReshowIntro_CapsTheNamesAndCountsTheRest()
    {
        var intro = BAChatService.ScreenScopeReshowIntro(
            new List<string> { "Màn A", "Màn B", "Màn C", "Màn D", "Màn E", "Màn F" }, new List<string>());

        Assert.Contains("6 màn hình", intro);
        Assert.Contains("và 2 mục khác", intro);
        Assert.DoesNotContain("Màn E", intro);
    }
}
