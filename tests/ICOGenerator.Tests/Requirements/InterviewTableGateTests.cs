using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat (luồng hoặc thông báo), cộng các cổng của ba bảng BA tự soạn
// ngầm ở đầu lượt (báo cáo, thiết kế UX, phân quyền). Ba điều phải giữ bằng test, và cả ba là những chỗ
// mà một lần sửa vô ý sẽ làm hỏng cả buổi phỏng vấn chứ không chỉ một lượt:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → báo cáo → thiết kế UX → phân quyền. Mọi tác vụ đều trỏ về bước
//     luồng (ô "phục vụ bước nào"); mỗi báo cáo là một tác vụ nên bảng báo cáo phải đứng trước lượt thiết
//     kế; các DÒNG của bảng phân quyền chính là các tác vụ của bản thiết kế.
//  2. KHÔNG BAO GIỜ hai bảng cùng lượt: hai khối "## LƯỢT NÀY:" là hai mệnh lệnh chọi nhau. Và không bảng
//     thiết kế nào (màn hình, phân quyền) được bày ra cho người dùng chốt — Select chỉ còn luồng/thông báo.
//  3. KHÔNG KHÓA CHÉO. PermissionMatrixGate phải bỏ qua CẢ HAI dòng chốt-bằng-bảng (phân quyền và thông
//     báo) vì chúng chỉ [RÕ] khi bảng của chúng đã có. Test dưới đây chốt rằng khi cổng phân quyền mở thì
//     các cổng kia cũng mở được — tức chúng luôn chạy TRƯỚC nó — và rằng nhóm thông báo ở [CHƯA HỎI] KHÔNG
//     chặn cổng nào. Cùng họ với nó là luật "chờ NGÃ NGŨ, không chờ SẴN SÀNG" của cổng màn hình: một nhóm ở
//     [KHÔNG ÁP DỤNG] làm cổng đứng trước đóng vĩnh viễn, và chờ một cổng như thế là xoá luôn phạm vi màn
//     hình khỏi buổi phỏng vấn.
public class InterviewTableGateTests
{
    private const int Scope = 2; // số tác vụ của bản thiết kế — các dòng của bảng phân quyền

    // Bản đồ ở trạng thái CUỐI BUỔI: mọi nhóm áp dụng đã [RÕ] trừ chính dòng phân quyền.
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
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
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

    // Bảng BÁO CÁO được ráp ngầm TRƯỚC lượt soạn phạm vi. Nhóm đã [RÕ] mà bảng chưa có (lời gọi ráp vừa
    // lỗi) ⇒ lượt soạn phạm vi CHỜ — soạn lúc này là chốt một phạm vi thiếu các màn hình báo cáo, và bảng
    // phân quyền thì thiếu luôn dòng quyền của chúng. Lượt kế ráp lại.
    [Fact]
    public void UxDesign_WaitsWhileTheReportMapIsStillToBeDrafted()
    {
        var project = ProjectWith(coverage: ReportsClear, flowMap: ConfirmedFlow);

        Assert.True(ReportMapGate.ShouldDraft(project));
        Assert.False(UxDesignGate.ShouldDesign(project));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(project));
    }

    // Không có báo cáo nào ([KHÔNG ÁP DỤNG]) ⇒ lượt soạn phạm vi chạy ngay sau bảng luồng — và KHÔNG bảng nào
    // được bày ra cho người dùng: phạm vi màn hình là thiết kế của BA.
    [Fact]
    public void UxDesign_RunsRightAfterFlow_WhenThereAreNoReports_WithoutShowingATable()
    {
        var project = ProjectWith(flowMap: ConfirmedFlow);

        Assert.True(UxDesignGate.ShouldDesign(project));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(project));
    }

    // PHẠM VI MÀN HÌNH ĐỨNG SAU BẢNG GIEO RA MÀN HÌNH.
    [Fact]
    public void UxDesign_RunsOnceTheReportMapIsDrafted()
    {
        Assert.True(UxDesignGate.ShouldDesign(ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports)));
    }

    // Bảng phân quyền đứng trên phạm vi ĐÃ CHỐT: còn mục chưa chốt thì chờ, chốt xong thì soạn — và cũng
    // không bày ra trong khung chat.
    [Fact]
    public void PermissionDraft_RunsAfterTheBlueprintIsDesigned()
    {
        Assert.False(PermissionMatrixGate.ShouldDraft(ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports)));

        var settled = ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports, blueprint: Designed);
        Assert.True(PermissionMatrixGate.ShouldDraft(settled));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(settled));
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

    // ==== KHÔNG KHÓA CHÉO ====

    // Điều kiện BẢN ĐỒ của cổng phân quyền đúng ⇒ điều kiện bản đồ của các cổng kia đương nhiên cũng đúng.
    // Đây là bất biến giữ cho chuỗi không bao giờ bỏ sót bảng nào: bảng chưa có sẽ lần lượt được làm trước
    // cổng cuối.
    [Fact]
    public void EveryEarlierGateIsOpenWheneverThePermissionGateIs()
    {
        var project = ProjectWith(coverage: ReportsClear);

        Assert.True(PermissionMatrixGate.ShouldDraft(ReportsClear, null, Scope));
        Assert.True(FlowMapGate.ShouldAsk(project));
        Assert.True(ReportMapGate.ShouldDraft(project));
        Assert.True(UxDesignGate.ShouldDesign(ReportsClear, null, null, null));
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

    // Đã thiết kế xong ⇒ cổng ĐÓNG cho tới khi phạm vi trôi thật: một tác vụ chưa xếp chỗ, hay một bước luồng
    // mới chưa có tác vụ nào. Thiết kế lại một bản y hệt là bốn lời gọi LLM cho không.
    [Fact]
    public void UxDesignGate_StaysClosedOnceDesigned_UntilTheScopeDrifts()
    {
        Assert.False(UxDesignGate.ShouldDesign(EverythingClear, Designed, ConfirmedFlow, ConfirmedReports));

        var drifted = UxBlueprintBuilder.Parse(Designed);
        drifted.Tasks.Add(new UxTask { Id = "T-04", Name = "Huỷ kế hoạch", Placed = false });
        Assert.True(UxDesignGate.ShouldDesign(EverythingClear, UxBlueprintBuilder.Serialize(drifted), ConfirmedFlow, ConfirmedReports));

        var newStep = ConfirmedFlow.Replace("]}]", ",{\"actor\":\"HR Assistant\",\"action\":\"Gửi kế hoạch cho nhân viên\",\"outcome\":\"\",\"included\":true}]}]");
        Assert.True(UxDesignGate.ShouldDesign(EverythingClear, Designed, newStep, ConfirmedReports));
    }

    // LẦN SOẠN ĐẦU của phạm vi màn hình phải NHƯỜNG các bảng đứng trước. Ca thật (dự án JD Libary 1, thời bảng
    // màn hình còn bày ra): người dùng kể
    // luồng ngay lượt 3 nên «Chức năng & luồng nghiệp vụ chính» lên [RÕ] rất sớm, trong khi vai trò còn
    // phải hỏi thêm mấy lượt — cổng luồng ĐÓNG, cổng màn hình mở, và bảng màn hình bày ở lượt 12 còn bảng
    // luồng mãi lượt 20. Cột "màn này phục vụ bước nào" của lượt 12 ra rỗng vì chưa có bước nào tồn tại để
    // gắn, nên khi luồng chốt xong thì UncoveredActions báo gần như MỌI bước chưa ai phụ trách và người
    // dùng phải rà bảng màn hình lần thứ hai — chỉ vì lần đầu bày quá sớm. Cổng mượn điều kiện của cổng
    // luồng (UxDesignGate.FirstDraftReady).
    [Fact]
    public void UxDesignGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
        Assert.False(UxDesignGate.ShouldDesign(coverage, null, null, null));
        // Lượt này KHÔNG có bảng nào: BA hỏi tiếp như một lượt chat thường. Bản đồ ở đây cho MỌI nhóm khác
        // [RÕ], nên khẳng định `None` cũng là khẳng định không cổng nào khác chen vào chỗ trống.
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage)));
    }

    // VẾ THỨ NHẤT: cổng màn hình chờ nhóm «Dữ liệu / danh mục chính» NGÃ NGŨ. Mỗi danh mục ứng dụng tự quản
    // lý là một màn hình quản lý riêng, do lượt chắt lọc phạm vi bắt từ hội thoại — soạn phạm vi trước khi
    // hội thoại kể xong các danh mục là chốt một phạm vi biết trước là sẽ thiếu.
    [Fact]
    public void UxDesignGate_StaysClosedUntilTheDataGroupIsSettled()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [MỘT PHẦN] còn thiếu: danh mục nào ứng dụng tự quản lý.");

        Assert.False(UxDesignGate.ShouldDesign(coverage, null, null, null));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // VẾ THỨ HAI: cổng màn hình cũng chờ nhóm «Báo cáo / thống kê» đã CHẠM TỚI, vì mỗi báo cáo còn giữ
    // là một màn hình. Chỉ đòi chạm tới chứ không đòi [RÕ]: [KHÔNG ÁP DỤNG] nghĩa là sẽ không bao giờ có
    // bảng báo cáo, và chờ nó là chờ vĩnh viễn (ca đó chạy qua UxDesign_RunsRightAfterFlow_WhenThereAreNoReports_WithoutShowingATable).
    [Fact]
    public void UxDesignGate_StaysClosedUntilTheReportGroupIsSettled()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] Có cần báo cáo. còn thiếu: gồm những báo cáo nào");

        Assert.False(UxDesignGate.ShouldDesign(coverage, null, null, null));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // MẶT TRÁI của hai vế trên, và là chỗ dễ vá sai nhất: cổng màn hình chờ nhóm NGÃ NGŨ, KHÔNG phải chờ nó
    // [RÕ]. Nhóm «Dữ liệu / danh mục chính» ở [KHÔNG ÁP DỤNG] sẽ không bao giờ lên [RÕ], trong khi
    // PermissionMatrixGate coi [KHÔNG ÁP DỤNG] là đã trả lời. Chờ nhầm vế thì phạm vi màn hình không bao giờ
    // được soạn, bảng phân quyền (chờ phạm vi chốt) cũng không, và nút "Write Requirement" không bao giờ sáng.
    [Fact]
    public void UxDesignGate_StillOpens_WhenAnEarlierGroupIsNotApplicable()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [KHÔNG ÁP DỤNG] Không có danh mục nào.");

        Assert.True(UxDesignGate.ShouldDesign(coverage, null, null, null));
        Assert.True(UxDesignGate.ShouldDesign(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // Điều kiện BẢN ĐỒ của cổng màn hình mở cùng lúc với các cổng đứng trước — nhưng lượt soạn (bản nhận
    // Project) còn CHỜ bảng luồng được người dùng chốt: ô "phục vụ bước nào" trỏ vào chính các bước ấy, và
    // một lượt soạn không có trọng tài nào đứng trên để nhường thứ tự như Select.
    [Fact]
    public void UxDesignGate_MapConditionOpensWithTheEarlierGates_ButTheDraftWaitsForTheFlowTable()
    {
        Assert.True(FlowMapGate.ShouldAsk(EverythingClear, null));
        Assert.True(ReportMapGate.ShouldDraft(ReportsClear, null));
        Assert.True(UxDesignGate.ShouldDesign(EverythingClear, null, null, null));

        Assert.False(UxDesignGate.ShouldDesign(ProjectWith()));
        Assert.Equal(InterviewTableKind.FlowMap, InterviewTableGate.Select(ProjectWith()));
    }

    // BẤT BIẾN CHỐNG KHÓA CHÉO của chuỗi hai bảng cuối: cổng phân quyền phải BỎ QUA dòng thông báo khi xét "cuối buổi", nếu
    // không nó đứng im chờ một nhóm chỉ [RÕ] sau bảng của chính nó — mà bảng ấy lại đứng sau bảng phân
    // quyền.
    [Fact]
    public void PermissionMatrixGate_OpensWhileNotificationsAreStillUnasked()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Thông báo / nhắc nhở: [CHƯA HỎI]");

        Assert.True(PermissionMatrixGate.ShouldDraft(coverage, null, Scope));
    }

    // Bản đồ trống (dự án vừa tạo, hoặc vừa "New Chat") ⇒ mọi cổng đóng. Fail-closed: bày một bảng dựng
    // trên hư không là cách nhanh nhất để thu về một chữ ký cho phán đoán của BA.
    [Fact]
    public void Select_ReturnsNoneWithoutACoverageMap()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(coverage: "")));
    }
}
