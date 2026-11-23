using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat (nay chỉ còn bảng luồng), cổng của bảng báo cáo BA ráp ngầm ở
// đầu lượt, và cổng "bản thiết kế còn đúng không" của bước "Write Requirement". Ba điều phải giữ bằng test:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → báo cáo trong buổi phỏng vấn; thiết kế UX → phân quyền ở bước
//     "Write Requirement". Bảng báo cáo mượn điều kiện của cổng luồng nên không bao giờ đi trước nó.
//  2. KHÔNG BAO GIỜ hai bảng cùng lượt, và không bảng thiết kế nào được bày ra trong chat.
//  3. Bấm lại "Write Requirement" mà phạm vi không đổi thì KHÔNG thiết kế lại (UxDesignGate.IsStale).
public class InterviewTableGateTests
{
    // Bản đồ ở trạng thái CUỐI BUỔI: mọi nhóm áp dụng đã [RÕ].
    private static readonly string EverythingClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo.
        - ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant lập, HOD HR duyệt.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý.
        - Quy trình hiện tại & điểm khó: [RÕ] Làm tay trên Excel.
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HOD từ chối thì Assistant sửa lại.
        - Dữ liệu / danh mục chính: [RÕ] Khóa học, người học, đơn vị.
        - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Sĩ số tối đa 20.
        - Vòng đời & trạng thái: [RÕ] Nháp → Chờ duyệt → Đã duyệt.
        - Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần.
        - Quy mô sử dụng: [RÕ] Khoảng 200 người.
        """);

    // coverage = null ⇒ EverythingClear. Không đặt thẳng làm giá trị mặc định được nữa: bản đồ nay dựng
    // qua CoverageMapFixture nên nó không còn là hằng số biên dịch.
    private static Project ProjectWith(
        string? coverage = null,
        string? flowMap = null,
        string? blueprint = null,
        string? reportMap = null,
        string? permissionMatrix = null)
        => new()
        {
            RequirementCoverageMap = coverage ?? EverythingClear,
            FlowMap = flowMap,
            // Mặc định CHƯA thiết kế — đúng trạng thái của một dự án đang giữa buổi phỏng vấn.
            UxBlueprint = blueprint,
            ReportMap = reportMap,
            PermissionMatrix = permissionMatrix
        };

    private const string ConfirmedFlow = """
        [{"name":"Lập kế hoạch","kind":"luồng chính","role":"HR Assistant","steps":[
          {"actor":"HR Assistant","action":"Tạo kế hoạch quý","outcome":"Nháp","included":true},
          {"actor":"HOD HR","action":"Duyệt kế hoạch","outcome":"Đã duyệt","included":true}]}]
        """;

    // Bản thiết kế ĐÃ XONG: mọi tác vụ đã có chỗ, mọi bước luồng có tác vụ phụ trách.
    private static readonly string Designed = UxBlueprintBuilder.Serialize(new UxBlueprint
    {
        Version = 1,
        Tasks =
        {
            new UxTask { Id = "T-01", Name = "Tạo kế hoạch quý", FlowSteps = { "Tạo kế hoạch quý" }, Placed = true },
            new UxTask { Id = "T-02", Name = "Duyệt kế hoạch", FlowSteps = { "Duyệt kế hoạch" }, Placed = true },
            new UxTask { Id = "T-03", Name = "Báo cáo tiến độ đào tạo — để biết đơn vị nào chưa đạt", Kind = UxTaskKind.Report, Placed = true }
        },
        Screens =
        {
            new UxScreen { Id = "S-01", Name = "Training Plans", Actions = { new UxAction { Task = "T-01" }, new UxAction { Task = "T-02" }, new UxAction { Task = "T-03" } } }
        }
    });

    private const string ConfirmedReports =
        """[{"report":"Báo cáo tiến độ đào tạo","question":"để biết đơn vị nào chưa đạt","source":"Kế hoạch đào tạo","included":true}]""";

    // Bản đồ CUỐI BUỔI của một dự án CÓ báo cáo. EverythingClear để nhóm này ở [KHÔNG ÁP DỤNG] — đó là ca
    // "dự án không cần báo cáo nào", ca duy nhất mà bảng báo cáo không bao giờ được bày.
    private static readonly string ReportsClear = CoverageMapFixture.With(
        EverythingClear,
        "- Báo cáo / thống kê: [RÕ] Tiến độ đào tạo theo quý cho HOD.");

    private const string ConfirmedPermissions =
        """[{"taskId":"T-01","screen":"Training Plans","function":"Xem","grants":[{"role":"HOD HR","scope":"tất cả"}]}]""";

    // ==== THỨ TỰ ====

    [Fact]
    public void Select_AsksFlowMapFirst()
    {
        Assert.Equal(InterviewTableKind.FlowMap, InterviewTableGate.Select(ProjectWith()));
    }

    // Thiết kế không chạy trong buổi phỏng vấn: hết bảng luồng là lượt chat thường, kể cả khi chưa có bản
    // thiết kế nào — bản thiết kế được dựng ở bước "Write Requirement".
    [Fact]
    public void NoDesignTableIsEverShown_EvenBeforeTheBlueprintExists()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(flowMap: ConfirmedFlow)));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports)));
    }

    // Không còn bảng thông báo: hết bảng luồng là hết bảng bày ra, kể cả khi bảng phân quyền đã có và mọi
    // nhóm đã [RÕ] — đúng chỗ mà trước đây bảng thông báo được bày. Nhóm «Thông báo / nhắc nhở» không chốt
    // ở buổi phỏng vấn (CoverageConfirmedTableGuard đặt nó về trạng thái không chặn).
    [Fact]
    public void Select_ReturnsNoneOnceTheFlowIsConfirmed_EvenWithoutANotificationTable()
    {
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, blueprint: Designed,
                permissionMatrix: ConfirmedPermissions)));
    }

    // ==== CỔNG BẢNG BÁO CÁO ====

    // Ca "dự án không cần báo cáo nào": [KHÔNG ÁP DỤNG] KHÔNG phải [RÕ] ⇒ cổng không bao giờ mở. Thiếu lối
    // thoát này thì một ứng dụng thuần nhập liệu vẫn bị bày ra một bảng báo cáo rỗng ở cuối buổi.
    [Fact]
    public void ReportMapGate_StaysClosedWhenTheGroupIsNotApplicable()
    {
        Assert.False(ReportMapGate.ShouldDraft(EverythingClear, null));
    }

    // Và nó cũng đóng khi nhóm mới ở [MỘT PHẦN]: bảng này KHÔNG đi khai thác thay hội thoại. Bày sớm là bày
    // một bảng trống, tức bắt người dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được
    // chữ nào — thu về ít hơn cả cái ô kể tự do mà bảng thay thế.
    [Fact]
    public void ReportMapGate_StaysClosedWhileTheGroupIsOnlyPartlyAnswered()
    {
        var partial = CoverageMapFixture.With(
            EverythingClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] Có cần báo cáo. còn thiếu: gồm những báo cáo nào");

        Assert.False(ReportMapGate.ShouldDraft(partial, null));
    }

    // Cổng báo cáo mượn điều kiện của cổng luồng: nhóm báo cáo lên [RÕ] sớm (người dùng kể nhu cầu báo cáo
    // ngay lượt đầu) không được đẩy bảng báo cáo lên trước bảng luồng.
    [Fact]
    public void ReportMapGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            ReportsClear,
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(ReportMapGate.ShouldDraft(coverage, null));
        Assert.True(ReportMapGate.ShouldDraft(ReportsClear, null));
    }

    // ==== ĐIỀU KIỆN MỞ CỦA TỪNG CỔNG ====

    // Bảng luồng chở phần NGOẠI LỆ, và bày nó ra khi chưa ai hỏi tới đường hỏng nào là mời model bịa một
    // ngoại lệ để lấp chỗ trống — rồi người dùng gật cho xong.
    [Fact]
    public void FlowMapGate_StaysClosedWhileExceptionsAreUnasked()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
    }

    [Fact]
    public void FlowMapGate_StaysClosedWhileTheMainFlowIsStillOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: ai duyệt.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
    }

    // ==== CỔNG "BẢN THIẾT KẾ CÒN ĐÚNG KHÔNG" (bước Write Requirement) ====

    // Chưa thiết kế lần nào ⇒ cũ. Đã thiết kế và hội thoại không có lượt mới ⇒ còn đúng: bấm lại nút không
    // gọi model lần nào cho phần thiết kế.
    [Fact]
    public void UxDesignGate_IsStale_UntilDesigned_ThenOnlyWhenTheConversationGrows()
    {
        Assert.True(UxDesignGate.IsStale(ProjectWith(flowMap: ConfirmedFlow), turnCount: 10));

        var designed = ProjectWith(coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports, blueprint: Designed);
        designed.UxDesignedTurnCount = 10;
        Assert.False(UxDesignGate.IsStale(designed, turnCount: 10));
        Assert.True(UxDesignGate.IsStale(designed, turnCount: 12));
    }

    // Phạm vi trôi ngay trên các bảng (không cần lượt hội thoại mới): một tác vụ chưa xếp chỗ, hay một bước
    // luồng mới chưa có tác vụ nào, hay bảng báo cáo đổi.
    [Fact]
    public void UxDesignGate_NeedsRedesign_WhenTheTablesDrift()
    {
        Assert.False(UxDesignGate.NeedsRedesign(Designed, ConfirmedFlow, ConfirmedReports));

        var drifted = UxBlueprintBuilder.Parse(Designed);
        drifted.Tasks.Add(new UxTask { Id = "T-04", Name = "Huỷ kế hoạch", Placed = false });
        Assert.True(UxDesignGate.NeedsRedesign(UxBlueprintBuilder.Serialize(drifted), ConfirmedFlow, ConfirmedReports));

        var newStep = ConfirmedFlow.Replace("]}]", ",{\"actor\":\"HR Assistant\",\"action\":\"Gửi kế hoạch cho nhân viên\",\"outcome\":\"\",\"included\":true}]}]");
        Assert.True(UxDesignGate.NeedsRedesign(Designed, newStep, ConfirmedReports));

        Assert.True(UxDesignGate.NeedsRedesign(Designed, ConfirmedFlow, null));
    }

    // Sau khi Brief đã duyệt thì chỉ PATCH — người dùng đã duyệt một tài liệu liệt kê các màn này.
    [Fact]
    public void UxDesignGate_Mode_PatchesOnlyAfterTheBriefWasApproved()
    {
        var project = ProjectWith(blueprint: Designed);
        Assert.Equal(UxDesignMode.Design, UxDesignGate.Mode(project));

        project.BriefApprovedTurnCount = 8;
        Assert.Equal(UxDesignMode.Patch, UxDesignGate.Mode(project));
    }

    // Bản đồ trống (dự án vừa tạo, hoặc vừa "New Chat") ⇒ mọi cổng đóng. Fail-closed: bày một bảng dựng
    // trên hư không là cách nhanh nhất để thu về một chữ ký cho phán đoán của BA.
    [Fact]
    public void Select_ReturnsNoneWithoutACoverageMap()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(coverage: "")));
    }
}
