using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat. Ba điều phải giữ bằng test, và cả ba là những chỗ mà một lần
// sửa vô ý sẽ làm hỏng cả buổi phỏng vấn chứ không chỉ một lượt:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo. Mọi
//     bảng đều trỏ về bước luồng (bảng màn hình có ô "chức năng này phục vụ bước nào", cột "khi nào chuyển
//     vào" của bảng đối tượng lấy từ chính các bước); bảng đối tượng và bảng báo cáo GIEO RA màn hình
//     (danh mục "ứng dụng tự quản lý" và mỗi báo cáo còn giữ đều gieo vào bảng màn hình) nên phải đứng trước
//     bảng chốt phạm vi màn hình — đứng sau thì người dùng chốt "đây là toàn bộ màn hình" rồi mới thấy
//     danh sách dài thêm sau lưng; các DÒNG của bảng phân quyền chính là màn hình; và bảng thông báo vay
//     cả hai chiều (dòng = chuyển trạng thái của bảng đối tượng, mục chọn = vai trò của bảng phân quyền).
//     Hỏi ngược là bày ra một bảng mà chính BA cũng chưa đủ dữ kiện để điền sẵn.
//  2. KHÔNG BAO GIỜ hai bảng cùng lượt: hai khối "## LƯỢT NÀY:" là hai mệnh lệnh chọi nhau.
//  3. KHÔNG KHÓA CHÉO. Đây là cái bẫy đắt nhất: PermissionMatrixGate phải bỏ qua CẢ HAI dòng chốt-bằng-bảng
//     (phân quyền và thông báo) vì chúng chỉ [RÕ] sau khi bảng của chúng chốt. Nếu bốn bảng giữa cũng được
//     cho luật "chưa có bảng ⇒ không bao giờ [RÕ]" thì cổng (đòi nhóm [RÕ]) và bản đồ (đòi có bảng) khóa
//     chặt nhau. Test dưới đây chốt rằng khi cổng phân quyền mở thì các cổng kia cũng mở được — tức chúng
//     luôn được hỏi TRƯỚC nó — và rằng nhóm thông báo ở [CHƯA HỎI] KHÔNG chặn cổng nào. Cùng họ với nó là
//     luật "chờ NGÃ NGŨ, không chờ SẴN SÀNG" của cổng màn hình: một nhóm ở [KHÔNG ÁP DỤNG] làm cổng đứng
//     trước đóng vĩnh viễn, và chờ một cổng như thế là xoá luôn bảng màn hình khỏi buổi phỏng vấn.
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
        string? entityMap = null,
        string? reportMap = null,
        string? permissionMatrix = null,
        string? notificationMap = null)
        => new()
        {
            RequirementCoverageMap = coverage ?? EverythingClear,
            FlowMap = flowMap,
            // Mặc định là bảng màn hình CHƯA AI RÀ: phạm vi đã chắt được nhưng chưa qua tay người dùng —
            // đúng trạng thái của một dự án đang giữa buổi phỏng vấn, và là điều kiện để cổng màn hình mở.
            ScreenScopeMap = screenScope ?? PendingScreens,
            EntityMap = entityMap,
            ReportMap = reportMap,
            PermissionMatrix = permissionMatrix,
            NotificationMap = notificationMap
        };

    private const string ConfirmedFlow = """
        [{"name":"Lập kế hoạch","kind":"luồng chính","role":"HR Assistant","steps":[
          {"actor":"HR Assistant","action":"Tạo kế hoạch quý","outcome":"Nháp","included":true},
          {"actor":"HOD HR","action":"Duyệt kế hoạch","outcome":"Đã duyệt","included":true}]}]
        """;

    // Phạm vi đã chắt nhưng CHƯA AI RÀ — mọi dòng còn chờ duyệt.
    private const string PendingScreens =
        """[{"screen":"Màn hình Training Plan","purpose":"","functions":[],"included":true,"confirmedByUser":false}]""";

    private const string ConfirmedScreens = """
        [{"screen":"Màn hình Training Plan","purpose":"Lập kế hoạch",
          "functions":[{"name":"Xem","included":true,"confirmedByUser":true},
                       {"name":"Tạo","flowSteps":["Tạo kế hoạch quý"],"included":true,"confirmedByUser":true}],
          "included":true,"confirmedByUser":true}]
        """;

    private const string ConfirmedEntities = """
        [{"entity":"Kế hoạch đào tạo","description":"Kế hoạch lớp học theo quý",
          "fields":[{"name":"Quý","meaning":"Quý áp dụng","used":true}],
          "states":[{"state":"Nháp","entryCondition":"vừa tạo"},
                    {"state":"Đã duyệt","entryCondition":"HOD duyệt"}],
          "included":true}]
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

    [Fact]
    public void Select_AsksEntityMapAfterFlowIsConfirmed()
    {
        Assert.Equal(InterviewTableKind.EntityMap,
            InterviewTableGate.Select(ProjectWith(flowMap: ConfirmedFlow)));
    }

    // Bảng BÁO CÁO đứng giữa bảng đối tượng và bảng màn hình, và thứ tự đó là thứ tự PHỤ THUỘC ở cả hai
    // đầu: sau đối tượng vì ô "lấy số từ" trỏ về một đối tượng đã chốt; TRƯỚC màn hình vì mỗi báo cáo còn
    // tích là một MÀN HÌNH, và các màn hình ấy phải có mặt ngay ở lần bày ĐẦU của bảng màn hình.
    [Fact]
    public void Select_AsksReportMapAfterTheEntityMap()
    {
        Assert.Equal(InterviewTableKind.ReportMap,
            InterviewTableGate.Select(ProjectWith(
                coverage: ReportsClear,
                flowMap: ConfirmedFlow, entityMap: ConfirmedEntities)));
    }

    // BẢNG MÀN HÌNH ĐỨNG SAU HAI BẢNG GIEO RA MÀN HÌNH. Đây là bất biến chính của lần sửa thứ tự: chốt
    // phạm vi màn hình trước bảng đối tượng nghĩa là người dùng gật "đây là toàn bộ màn hình", rồi mấy lượt
    // sau bảng đối tượng gieo thêm màn hình quản lý danh mục vào bảng màn hình và bảng phải mở lại — kèm một
    // mâu thuẫn giả ở cổng KHÔNG MÂU THUẪN ("trước đây anh/chị xác nhận đây là toàn bộ màn hình…").
    [Fact]
    public void Select_AsksScreenScopeAfterTheEntityAndReportMaps()
    {
        Assert.Equal(InterviewTableKind.ScreenScope,
            InterviewTableGate.Select(ProjectWith(
                coverage: ReportsClear,
                flowMap: ConfirmedFlow, entityMap: ConfirmedEntities, reportMap: ConfirmedReports)));
    }

    [Fact]
    public void Select_AsksPermissionMatrixAfterTheScreenScope()
    {
        Assert.Equal(InterviewTableKind.PermissionMatrix,
            InterviewTableGate.Select(ProjectWith(
                coverage: ReportsClear,
                flowMap: ConfirmedFlow, entityMap: ConfirmedEntities, reportMap: ConfirmedReports,
                screenScope: ConfirmedScreens)));
    }

    // Bảng phân quyền là cổng ÁP CHÓT. Dòng phân quyền chỉ [RÕ] khi bảng chốt, nên không có đường nào
    // soạn tài liệu mà bỏ qua các bảng trước. Ca ở đây là dự án KHÔNG có báo cáo (nhóm [KHÔNG ÁP DỤNG]):
    // chuỗi rút còn luồng → đối tượng → màn hình → phân quyền.
    [Fact]
    public void Select_AsksPermissionMatrixAfterTheEntityMap()
    {
        Assert.Equal(InterviewTableKind.PermissionMatrix,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens, entityMap: ConfirmedEntities)));
    }

    // Bảng THÔNG BÁO là cổng cuối cùng, và nó xét theo BẢNG PHÂN QUYỀN ĐÃ CHỐT chứ không theo thứ tự ưu
    // tiên: một lượt bày bảng phân quyền hỏng không được phép để bảng này chen lên trước với danh sách vai
    // trò rỗng.
    [Fact]
    public void Select_AsksNotificationMapLast()
    {
        Assert.Equal(InterviewTableKind.NotificationMap,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens, entityMap: ConfirmedEntities,
                permissionMatrix: ConfirmedPermissions)));
    }

    [Fact]
    public void Select_ReturnsNoneWhenEveryTableIsConfirmed()
    {
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens, entityMap: ConfirmedEntities,
                permissionMatrix: ConfirmedPermissions, notificationMap: ConfirmedNotifications)));
    }

    // Dự án KHÔNG có vòng đời trạng thái nào (ứng dụng danh mục thuần): không dòng nào gieo được ⇒ bảng
    // này không bao giờ bày ra, và nhóm «Thông báo / nhắc nhở» quay về đường hỏi bằng câu hỏi. Thiếu lối
    // thoát này thì cả buổi kẹt: không bảng nào bày, không câu hỏi nào được phép, nút "Write Requirement"
    // không bao giờ sáng.
    [Fact]
    public void NotificationMapGate_StaysClosedWithoutAnyLifecycleState()
    {
        var catalogOnly = """
            [{"entity":"Khóa học","description":"Danh mục khóa học",
              "fields":[{"name":"Tên khóa","meaning":"Tên hiển thị","used":true}],
              "states":[],"included":true}]
            """;

        Assert.False(NotificationMapGate.ShouldAsk(null, ConfirmedPermissions, catalogOnly));
    }

    // …và nó cũng đóng khi bảng phân quyền chưa chốt: không có bảng đó thì danh sách người nhận chỉ còn
    // bốn mục quan hệ, thiếu hẳn phần "Toàn bộ <vai>".
    [Fact]
    public void NotificationMapGate_StaysClosedUntilPermissionsAreConfirmed()
    {
        Assert.False(NotificationMapGate.ShouldAsk(null, null, ConfirmedEntities));
    }

    // ==== CỔNG BẢNG BÁO CÁO ====

    // Ca "dự án không cần báo cáo nào": [KHÔNG ÁP DỤNG] KHÔNG phải [RÕ] ⇒ cổng không bao giờ mở. Thiếu lối
    // thoát này thì một ứng dụng thuần nhập liệu vẫn bị bày ra một bảng báo cáo rỗng ở cuối buổi.
    [Fact]
    public void ReportMapGate_StaysClosedWhenTheGroupIsNotApplicable()
    {
        Assert.False(ReportMapGate.ShouldAsk(EverythingClear, null, ConfirmedEntities));
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

        Assert.False(ReportMapGate.ShouldAsk(partial, null, ConfirmedEntities));
    }

    // Chưa chốt bảng đối tượng thì ô "lấy số từ" không có gì để trỏ về. Vế này treo vào một artifact, nhưng
    // nó không mở thêm đường vòng nào: lúc đó EntityMapGate đang mở và thắng ưu tiên, nên lượt ấy vẫn là
    // một lượt bày bảng chứ không phải một lượt câm.
    [Fact]
    public void ReportMapGate_StaysClosedUntilTheEntityMapIsConfirmed()
    {
        Assert.False(ReportMapGate.ShouldAsk(ReportsClear, null, null));
        Assert.Equal(InterviewTableKind.EntityMap,
            InterviewTableGate.Select(ProjectWith(coverage: ReportsClear, flowMap: ConfirmedFlow)));
    }

    // ==== KHÔNG BAO GIỜ HAI BẢNG CÙNG LƯỢT ====

    // Lượt kể lại file bảng tính đã có việc riêng và chỉ có MỘT chỗ trả lời (hai chip xác nhận). Mọi cổng
    // nhường nó một lượt và mở lại ngay lượt sau — cùng ngoại lệ mà cổng phân quyền đã có sẵn, chỉ khác là
    // giờ nó áp cho cả bốn.
    [Fact]
    public void Select_YieldsTheTurnToTheSourceReadback()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(), suppressed: true));
    }

    // ==== KHÔNG KHÓA CHÉO ====

    // Cổng phân quyền mở ⇒ điều kiện của cả ba cổng kia đương nhiên cũng đúng. Đây là bất biến giữ cho
    // chuỗi bốn bảng không bao giờ bỏ sót bảng nào: bảng chưa chốt sẽ lần lượt được hỏi trước cổng cuối.
    [Fact]
    public void EveryEarlierGateIsOpenWheneverThePermissionGateIs()
    {
        var project = ProjectWith();

        Assert.True(PermissionMatrixGate.ShouldAsk(project));
        Assert.True(FlowMapGate.ShouldAsk(project));
        Assert.True(ScreenScopeGate.ShouldAsk(project));
        Assert.True(EntityMapGate.ShouldAsk(project));
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

    // Bảng chưa có dòng nào ⇒ không có gì để hỏi — cùng luật với bảng phân quyền.
    [Fact]
    public void ScreenScopeGate_StaysClosedWithoutAnyScreen()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(EverythingClear, null));
    }

    // LẦN BÀY ĐẦU của bảng màn hình phải NHƯỜNG các bảng đứng trước, và thứ tự ưu tiên ở Select KHÔNG đủ để
    // bảo đảm điều đó: nó chỉ phân xử được khi hai cổng cùng mở. Ca thật (dự án JD Libary 1): người dùng kể
    // luồng ngay lượt 3 nên «Chức năng & luồng nghiệp vụ chính» lên [RÕ] rất sớm, trong khi vai trò còn
    // phải hỏi thêm mấy lượt — cổng luồng ĐÓNG, cổng màn hình mở, và bảng màn hình bày ở lượt 12 còn bảng
    // luồng mãi lượt 20. Cột "màn này phục vụ bước nào" của lượt 12 ra rỗng vì chưa có bước nào tồn tại để
    // gắn, nên khi luồng chốt xong thì UncoveredActions báo gần như MỌI bước chưa ai phụ trách và người
    // dùng phải rà bảng màn hình lần thứ hai — chỉ vì lần đầu bày quá sớm. Cổng nay mượn điều kiện của cổng
    // ĐỐI TƯỢNG, mà điều kiện đó đã bao điều kiện của cổng luồng.
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
        Assert.False(ScreenScopeGate.ShouldAsk(coverage, PendingScreens));
        // Và hệ quả phải thấy được ở đúng chỗ người dùng thấy: lượt này KHÔNG có bảng nào, BA hỏi tiếp như
        // một lượt chat thường thay vì bày ra một bảng không gắn được bước nào. Bản đồ ở đây cho MỌI nhóm
        // khác [RÕ], nên khẳng định `None` cũng là khẳng định không cổng nào khác chen vào chỗ trống.
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage)));
    }

    // VẾ MỚI THỨ NHẤT: cổng màn hình chờ cổng ĐỐI TƯỢNG. «Dữ liệu / danh mục chính» còn [MỘT PHẦN] thì bảng
    // đối tượng chưa bày được, mà chính nó mới quyết định danh mục nào cần một màn hình quản lý riêng — bày
    // bảng màn hình lúc này là chốt một phạm vi biết trước là sẽ thiếu. Bản đồ ở đây cho mọi nhóm khác [RÕ]
    // và bảng luồng đã chốt, nên khẳng định `None` cũng là khẳng định không cổng nào chen vào chỗ trống.
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheEntityGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [MỘT PHẦN] còn thiếu: danh mục nào ứng dụng tự quản lý.");

        Assert.False(EntityMapGate.ShouldAsk(coverage, null));
        Assert.False(ScreenScopeGate.ShouldAsk(coverage, PendingScreens));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // VẾ MỚI THỨ HAI: cổng màn hình cũng chờ nhóm «Báo cáo / thống kê» đã CHẠM TỚI, vì mỗi báo cáo còn giữ
    // là một màn hình. Chỉ đòi chạm tới chứ không đòi [RÕ]: [KHÔNG ÁP DỤNG] nghĩa là sẽ không bao giờ có
    // bảng báo cáo, và chờ nó là chờ vĩnh viễn (ca đó chạy qua Select_AsksPermissionMatrixAfterTheEntityMap).
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheReportGroupIsSettled()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] Có cần báo cáo. còn thiếu: gồm những báo cáo nào");

        Assert.False(ScreenScopeGate.ShouldAsk(coverage, PendingScreens));
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                coverage: coverage, flowMap: ConfirmedFlow, entityMap: ConfirmedEntities)));
    }

    // MẶT TRÁI của hai vế trên, và là chỗ dễ vá sai nhất: cổng màn hình chờ cổng đứng trước NGÃ NGŨ, KHÔNG
    // phải chờ nó SẴN SÀNG. Nhóm «Dữ liệu / danh mục chính» ở [KHÔNG ÁP DỤNG] làm EntityMapGate đóng VĨNH
    // VIỄN (nó đòi [RÕ]), trong khi PermissionMatrixGate coi [KHÔNG ÁP DỤNG] là đã trả lời và cứ thế mở.
    // Chờ nhầm vế thì bảng màn hình biến mất khỏi buổi phỏng vấn và bảng phân quyền quay về đứng trên các
    // dòng CHƯA AI DUYỆT — đúng cái nền mà bảng màn hình sinh ra để vá.
    [Fact]
    public void ScreenScopeGate_StillOpens_WhenAnEarlierGroupIsNotApplicable()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Dữ liệu / danh mục chính: [KHÔNG ÁP DỤNG] Không có danh mục nào.");

        Assert.False(EntityMapGate.ShouldAsk(coverage, null));
        Assert.True(ScreenScopeGate.ShouldAsk(coverage, PendingScreens));
        Assert.Equal(InterviewTableKind.ScreenScope,
            InterviewTableGate.Select(ProjectWith(coverage: coverage, flowMap: ConfirmedFlow)));
    }

    // NỬA THỨ HAI của cùng lỗ hổng, ở cổng đối tượng. Hai nhóm của nó rời hẳn nhóm vai trò nên có ca dữ
    // liệu + vòng đời đã rõ trong khi vai trò còn [MỘT PHẦN]: cổng luồng đóng và bảng ĐỐI TƯỢNG bày ra đầu
    // tiên, trong khi cột "khi nào chuyển vào" của nó lấy điều kiện từ chính các bước luồng.
    [Fact]
    public void EntityMapGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(EntityMapGate.ShouldAsk(coverage, null));
    }

    // Nửa còn lại của cùng một luật: đủ điều kiện rồi thì CẢ BA cổng cùng mở, và Select mới là chỗ quyết
    // định bảng luồng đi trước. Điều kiện mượn KHÔNG được siết tới mức đòi bảng đứng trước đã CHỐT — làm
    // vậy là biến một lượt bày bảng hỏng (model không trả nổi bảng dùng được) thành chuỗi kẹt vĩnh viễn.
    [Fact]
    public void ScreenScopeGate_OpensAlongsideTheEarlierGates_NotAfterTheirTablesAreConfirmed()
    {
        Assert.True(FlowMapGate.ShouldAsk(EverythingClear, null));
        Assert.True(EntityMapGate.ShouldAsk(EverythingClear, null));
        Assert.True(ScreenScopeGate.ShouldAsk(EverythingClear, PendingScreens));
        Assert.Equal(InterviewTableKind.FlowMap, InterviewTableGate.Select(ProjectWith()));
    }

    // BẤT BIẾN CHỐNG KHÓA CHÉO của chuỗi hai bảng cuối. Nhóm «Thông báo / nhắc nhở» không còn được hỏi
    // bằng câu hỏi nên nó nằm ở [CHƯA HỎI] suốt buổi; nếu cổng đối tượng vẫn đòi nhóm đó chạm tới thì cả
    // ba khóa nhau — bảng đối tượng chờ nhóm thông báo, nhóm thông báo chờ bảng thông báo, bảng thông báo
    // chờ bảng phân quyền (mà cổng phân quyền lại chờ bảng đối tượng qua chuỗi ưu tiên).
    [Fact]
    public void EntityMapGate_OpensWhileNotificationsAreStillUnasked()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Thông báo / nhắc nhở: [CHƯA HỎI]");

        Assert.True(EntityMapGate.ShouldAsk(coverage, null));
    }

    // Nửa còn lại của cùng bất biến: cổng phân quyền phải BỎ QUA dòng thông báo khi xét "cuối buổi", nếu
    // không nó đứng im chờ một nhóm chỉ [RÕ] sau bảng của chính nó — mà bảng ấy lại đứng sau bảng phân
    // quyền.
    [Fact]
    public void PermissionMatrixGate_OpensWhileNotificationsAreStillUnasked()
    {
        var coverage = CoverageMapFixture.With(
            EverythingClear,
            "- Thông báo / nhắc nhở: [CHƯA HỎI]");

        Assert.True(PermissionMatrixGate.ShouldAsk(coverage, null, Scope));
    }

    // Bản đồ trống (dự án vừa tạo, hoặc vừa "New Chat") ⇒ mọi cổng đóng. Fail-closed: bày một bảng dựng
    // trên hư không là cách nhanh nhất để thu về một chữ ký cho phán đoán của BA.
    [Fact]
    public void Select_ReturnsNoneWithoutACoverageMap()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(coverage: "")));
    }
}
