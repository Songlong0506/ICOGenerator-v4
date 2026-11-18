using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat (luồng hoặc thông báo), cộng các cổng của ba bảng BA tự soạn
// ngầm ở đầu lượt (báo cáo, phạm vi màn hình, phân quyền). Ba điều phải giữ bằng test, và cả ba là những chỗ
// mà một lần sửa vô ý sẽ làm hỏng cả buổi phỏng vấn chứ không chỉ một lượt:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → báo cáo → màn hình → phân quyền → thông báo. Mọi bảng đều trỏ
//     về bước luồng (phạm vi màn hình có ô "chức năng này phục vụ bước nào"); bảng báo cáo GIEO RA màn hình
//     nên phải đứng trước lượt soạn phạm vi; các DÒNG của bảng phân quyền chính là màn hình × chức năng; và
//     mục chọn người nhận của bảng thông báo là vai trò của bảng phân quyền.
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
    private static readonly List<string> Scope = new() { "Màn hình Training Plan" };

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
        string? screenScope = null,
        string? reportMap = null,
        string? permissionMatrix = null,
        string? notificationMap = null)
        => new()
        {
            RequirementCoverageMap = coverage ?? EverythingClear,
            FlowMap = flowMap,
            // Mặc định là bảng màn hình CHƯA CHỐT: phạm vi đã chắt được nhưng chưa qua lượt soạn — đúng
            // trạng thái của một dự án đang giữa buổi phỏng vấn, và là điều kiện để cổng màn hình mở.
            ScreenScopeMap = screenScope ?? PendingScreens,
            ReportMap = reportMap,
            PermissionMatrix = permissionMatrix,
            NotificationMap = notificationMap
        };

    private const string ConfirmedFlow = """
        [{"name":"Lập kế hoạch","kind":"luồng chính","role":"HR Assistant","steps":[
          {"actor":"HR Assistant","action":"Tạo kế hoạch quý","outcome":"Nháp","included":true},
          {"actor":"HOD HR","action":"Duyệt kế hoạch","outcome":"Đã duyệt","included":true}]}]
        """;

    // Phạm vi đã chắt nhưng CHƯA CHỐT.
    private const string PendingScreens =
        """[{"screen":"Màn hình Training Plan","purpose":"","functions":[],"included":true,"confirmedByUser":false}]""";

    private const string ConfirmedScreens = """
        [{"screen":"Màn hình Training Plan","purpose":"Lập kế hoạch",
          "functions":[{"name":"Xem","included":true,"confirmedByUser":true},
                       {"name":"Tạo","flowSteps":["Tạo kế hoạch quý"],"included":true,"confirmedByUser":true}],
          "included":true,"confirmedByUser":true}]
        """;

    private const string ConfirmedReports =
        """[{"report":"Báo cáo tiến độ đào tạo","question":"để biết đơn vị nào chưa đạt","source":"Kế hoạch đào tạo","included":true}]""";

    // Bản đồ CUỐI BUỔI của một dự án CÓ báo cáo. EverythingClear để nhóm này ở [KHÔNG ÁP DỤNG] — đó là ca
    // "dự án không cần báo cáo nào", ca duy nhất mà bảng báo cáo không bao giờ được bày.
    private static readonly string ReportsClear = CoverageMapFixture.With(
        EverythingClear,
        "- Báo cáo / thống kê: [RÕ] Tiến độ đào tạo theo quý cho HOD.");

    private const string ConfirmedPermissions =
        """[{"screen":"Màn hình Training Plan","function":"Xem","grants":[{"role":"HOD HR","scope":"tất cả"}]}]""";

    private const string ConfirmedNotifications =
        """[{"entity":"Kế hoạch đào tạo","event":"Đã duyệt","to":["Người tạo"],"needed":true}]""";

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
    public void ScreenDraft_WaitsWhileTheReportMapIsStillToBeDrafted()
    {
        var project = ProjectWith(coverage: ReportsClear, flowMap: ConfirmedFlow, screenScope: PendingScreens);

        Assert.True(ReportMapGate.ShouldDraft(project));
        Assert.False(ScreenScopeGate.ShouldDraft(project));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(project));
    }

    // Không có báo cáo nào ([KHÔNG ÁP DỤNG]) ⇒ lượt soạn phạm vi chạy ngay sau bảng luồng — và KHÔNG bảng nào
    // được bày ra cho người dùng: phạm vi màn hình là thiết kế của BA.
    [Fact]
    public void ScreenDraft_RunsRightAfterFlow_WhenThereAreNoReports_WithoutShowingATable()
    {
        var project = ProjectWith(flowMap: ConfirmedFlow);

        Assert.True(ScreenScopeGate.ShouldDraft(project));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(project));
    }

    // PHẠM VI MÀN HÌNH ĐỨNG SAU BẢNG GIEO RA MÀN HÌNH.
    [Fact]
    public void ScreenDraft_RunsOnceTheReportMapIsDrafted()
    {
        Assert.True(ScreenScopeGate.ShouldDraft(ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports)));
    }

    // Bảng phân quyền đứng trên phạm vi ĐÃ CHỐT: còn mục chưa chốt thì chờ, chốt xong thì soạn — và cũng
    // không bày ra trong khung chat.
    [Fact]
    public void PermissionDraft_RunsAfterTheScreenScopeIsSettled()
    {
        Assert.False(PermissionMatrixGate.ShouldDraft(ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports)));

        var settled = ProjectWith(
            coverage: ReportsClear, flowMap: ConfirmedFlow, reportMap: ConfirmedReports, screenScope: ConfirmedScreens);
        Assert.True(PermissionMatrixGate.ShouldDraft(settled));
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(settled));
    }

    // Bảng THÔNG BÁO là cổng cuối cùng, và nó xét theo BẢNG PHÂN QUYỀN ĐÃ CÓ: một lượt soạn bảng phân quyền
    // hỏng không được phép để bảng này chen lên trước với danh sách vai trò rỗng.
    [Fact]
    public void Select_AsksNotificationMapLast()
    {
        Assert.Equal(InterviewTableKind.NotificationMap,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens,
                permissionMatrix: ConfirmedPermissions)));
    }

    [Fact]
    public void Select_ReturnsNoneWhenEveryTableIsConfirmed()
    {
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens,
                permissionMatrix: ConfirmedPermissions, notificationMap: ConfirmedNotifications)));
    }

    // Dự án KHÔNG có vòng đời trạng thái nào (ứng dụng danh mục thuần — «Vòng đời & trạng thái» ở
    // [KHÔNG ÁP DỤNG]): không có sự kiện nào để ráp ⇒ bảng này không bao giờ bày ra, và nhóm «Thông báo /
    // nhắc nhở» quay về đường hỏi bằng câu hỏi. Thiếu lối thoát này thì cả buổi kẹt: không bảng nào bày,
    // không câu hỏi nào được phép, nút "Write Requirement" không bao giờ sáng.
    [Fact]
    public void NotificationMapGate_StaysClosedWithoutAnyLifecycle()
    {
        var catalogOnly = CoverageMapFixture.With(
            EverythingClear,
            "- Vòng đời & trạng thái: [KHÔNG ÁP DỤNG] Chỉ là danh mục, không có trạng thái.");

        Assert.False(NotificationMapGate.ShouldAsk(null, ConfirmedPermissions, catalogOnly));
        Assert.True(NotificationMapGate.ShouldAsk(null, ConfirmedPermissions, EverythingClear));
    }

    // …và nó cũng đóng khi bảng phân quyền chưa có: không có bảng đó thì danh sách người nhận chỉ còn bốn
    // mục quan hệ, thiếu hẳn phần "Toàn bộ <vai>".
    [Fact]
    public void NotificationMapGate_StaysClosedUntilPermissionsAreConfirmed()
    {
        Assert.False(NotificationMapGate.ShouldAsk(null, null, EverythingClear));
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
        Assert.True(ScreenScopeGate.ShouldDraft(ReportsClear, PendingScreens));
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

    // Bảng chưa có dòng nào ⇒ không có gì để soạn — cùng luật với bảng phân quyền.
    [Fact]
    public void ScreenScopeGate_StaysClosedWithoutAnyScreen()
    {
        Assert.False(ScreenScopeGate.ShouldDraft(EverythingClear, null));
    }

    // LẦN SOẠN ĐẦU của phạm vi màn hình phải NHƯỜNG các bảng đứng trước. Ca thật (dự án JD Libary 1, thời bảng
    // màn hình còn bày ra): người dùng kể
    // luồng ngay lượt 3 nên «Chức năng & luồng nghiệp vụ chính» lên [RÕ] rất sớm, trong khi vai trò còn
    // phải hỏi thêm mấy lượt — cổng luồng ĐÓNG, cổng màn hình mở, và bảng màn hình bày ở lượt 12 còn bảng
    // luồng mãi lượt 20. Cột "màn này phục vụ bước nào" của lượt 12 ra rỗng vì chưa có bước nào tồn tại để
    // gắn, nên khi luồng chốt xong thì UncoveredActions báo gần như MỌI bước chưa ai phụ trách và người
    // dùng phải rà bảng màn hình lần thứ hai — chỉ vì lần đầu bày quá sớm. Cổng mượn điều kiện của cổng
    // luồng (ScreenScopeGate.FirstDraftReady).
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
        Assert.False(ScreenScopeGate.ShouldDraft(coverage, PendingScreens));
        // Lượt này KHÔNG có bảng nào: BA hỏi tiếp như một lượt chat thường. Bản đồ ở đây cho MỌI nhóm khác
        // [RÕ], nên khẳng định `None` cũng là khẳng định không cổng nào khác chen vào chỗ trống.
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage)));
    }

    // VẾ THỨ NHẤT: cổng màn hình chờ nhóm «Dữ liệu / danh mục chính» NGÃ NGŨ. Mỗi danh mục ứng dụng tự quản
    // lý là một màn hình quản lý riêng, do lượt chắt lọc phạm vi bắt từ hội thoại — soạn phạm vi trước khi
    // hội thoại kể xong các danh mục là chốt một phạm vi biết trước là sẽ thiếu.
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheDataGroupIsSettled()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [MỘT PHẦN] còn thiếu: danh mục nào ứng dụng tự quản lý.");

        Assert.False(ScreenScopeGate.ShouldDraft(coverage, PendingScreens));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // VẾ THỨ HAI: cổng màn hình cũng chờ nhóm «Báo cáo / thống kê» đã CHẠM TỚI, vì mỗi báo cáo còn giữ
    // là một màn hình. Chỉ đòi chạm tới chứ không đòi [RÕ]: [KHÔNG ÁP DỤNG] nghĩa là sẽ không bao giờ có
    // bảng báo cáo, và chờ nó là chờ vĩnh viễn (ca đó chạy qua ScreenDraft_RunsRightAfterFlow_WhenThereAreNoReports_WithoutShowingATable).
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheReportGroupIsSettled()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] Có cần báo cáo. còn thiếu: gồm những báo cáo nào");

        Assert.False(ScreenScopeGate.ShouldDraft(coverage, PendingScreens));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // MẶT TRÁI của hai vế trên, và là chỗ dễ vá sai nhất: cổng màn hình chờ nhóm NGÃ NGŨ, KHÔNG phải chờ nó
    // [RÕ]. Nhóm «Dữ liệu / danh mục chính» ở [KHÔNG ÁP DỤNG] sẽ không bao giờ lên [RÕ], trong khi
    // PermissionMatrixGate coi [KHÔNG ÁP DỤNG] là đã trả lời. Chờ nhầm vế thì phạm vi màn hình không bao giờ
    // được soạn, bảng phân quyền (chờ phạm vi chốt) cũng không, và nút "Write Requirement" không bao giờ sáng.
    [Fact]
    public void ScreenScopeGate_StillOpens_WhenAnEarlierGroupIsNotApplicable()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [KHÔNG ÁP DỤNG] Không có danh mục nào.");

        Assert.True(ScreenScopeGate.ShouldDraft(coverage, PendingScreens));
        Assert.True(ScreenScopeGate.ShouldDraft(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // Điều kiện BẢN ĐỒ của cổng màn hình mở cùng lúc với các cổng đứng trước — nhưng lượt soạn (bản nhận
    // Project) còn CHỜ bảng luồng được người dùng chốt: ô "phục vụ bước nào" trỏ vào chính các bước ấy, và
    // một lượt soạn không có trọng tài nào đứng trên để nhường thứ tự như Select.
    [Fact]
    public void ScreenScopeGate_MapConditionOpensWithTheEarlierGates_ButTheDraftWaitsForTheFlowTable()
    {
        Assert.True(FlowMapGate.ShouldAsk(EverythingClear, null));
        Assert.True(ReportMapGate.ShouldDraft(ReportsClear, null));
        Assert.True(ScreenScopeGate.ShouldDraft(EverythingClear, PendingScreens));

        Assert.False(ScreenScopeGate.ShouldDraft(ProjectWith()));
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
