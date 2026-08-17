using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat. Ba điều phải giữ bằng test, và cả ba là những chỗ mà một lần
// sửa vô ý sẽ làm hỏng cả buổi phỏng vấn chứ không chỉ một lượt:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → màn hình → đối tượng → phân quyền → thông báo. Bảng màn hình
//     có ô "màn này phục vụ bước nào", các DÒNG của bảng phân quyền chính là màn hình, và bảng thông báo
//     vay cả hai chiều (dòng = chuyển trạng thái của bảng đối tượng, mục chọn = vai trò của bảng phân
//     quyền). Hỏi ngược là bày ra một bảng mà chính BA cũng chưa đủ dữ kiện để điền sẵn.
//  2. KHÔNG BAO GIỜ hai bảng cùng lượt: hai khối "## LƯỢT NÀY:" là hai mệnh lệnh chọi nhau.
//  3. KHÔNG KHÓA CHÉO. Đây là cái bẫy đắt nhất: PermissionMatrixGate phải bỏ qua CẢ HAI dòng chốt-bằng-bảng
//     (phân quyền và thông báo) vì chúng chỉ [RÕ] sau khi bảng của chúng chốt. Nếu ba bảng giữa cũng được
//     cho luật "chưa có bảng ⇒ không bao giờ [RÕ]" thì cổng (đòi nhóm [RÕ]) và bản đồ (đòi có bảng) khóa
//     chặt nhau. Test dưới đây chốt rằng khi cổng phân quyền mở thì cả ba cổng kia cũng mở được — tức
//     chúng luôn được hỏi TRƯỚC nó — và rằng nhóm thông báo ở [CHƯA HỎI] KHÔNG chặn cổng nào.
public class InterviewTableGateTests
{
    private static readonly List<string> Scope = new() { "Màn hình Training Plan" };

    // Bản đồ ở trạng thái CUỐI BUỔI: mọi nhóm áp dụng đã [RÕ] trừ chính dòng phân quyền.
    private const string EverythingClear = """
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo. {nguồn: "lên kế hoạch các lớp học"}
        - ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant lập, HOD HR duyệt. {nguồn: "Assistant lập, HOD duyệt"}
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý. {nguồn: "Đúng luồng này"}
        - Quy trình hiện tại & điểm khó: [RÕ] Làm tay trên Excel. {nguồn: "tự tính tay hay sai"}
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HOD từ chối thì Assistant sửa lại. {nguồn: "trả về sửa lại"}
        - Dữ liệu / danh mục chính: [RÕ] Khóa học, người học, đơn vị. {nguồn: "danh sách khóa học"}
        - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Sĩ số tối đa 20. {nguồn: "mỗi lớp tối đa 20"}
        - Vòng đời & trạng thái: [RÕ] Nháp → Chờ duyệt → Đã duyệt. {nguồn: "duyệt xong là khóa"}
        - Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit. {nguồn: "gửi mail cho HOD"}
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần. {nguồn: "hiện tại chưa cần"}
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        - Quy mô sử dụng: [RÕ] Khoảng 200 người. {nguồn: "tầm 200 nhân viên"}
        """;

    private static Project ProjectWith(
        string coverage = EverythingClear,
        string? flowMap = null,
        string? screenScope = null,
        string? entityMap = null,
        string? permissionMatrix = null,
        string? notificationMap = null)
        => new()
        {
            RequirementCoverageMap = coverage,
            // Cột PlannedScope lưu dạng bullet ("- …") — InterviewOutlookService.ParseItems bỏ mọi dòng
            // không có tiền tố đó, nên viết thẳng tên màn hình vào đây là dựng một dự án có phạm vi RỖNG.
            PlannedScope = string.Join("\n", Scope.Select(s => "- " + s)),
            FlowMap = flowMap,
            ScreenScopeMap = screenScope,
            EntityMap = entityMap,
            PermissionMatrix = permissionMatrix,
            NotificationMap = notificationMap
        };

    private const string ConfirmedFlow = """
        [{"name":"Lập kế hoạch","kind":"luồng chính","role":"HR Assistant","steps":[
          {"actor":"HR Assistant","action":"Tạo kế hoạch quý","outcome":"Nháp","included":true},
          {"actor":"HOD HR","action":"Duyệt kế hoạch","outcome":"Đã duyệt","included":true}]}]
        """;

    private const string ConfirmedScreens = """
        [{"screen":"Màn hình Training Plan","purpose":"Lập kế hoạch","functions":"Xem, Tạo","flowSteps":["Tạo kế hoạch quý"],"included":true}]
        """;

    private const string ConfirmedEntities = """
        [{"entity":"Kế hoạch đào tạo","description":"Kế hoạch lớp học theo quý",
          "fields":[{"name":"Quý","meaning":"Quý áp dụng","used":true}],
          "states":[{"state":"Nháp","entryCondition":"vừa tạo"},
                    {"state":"Đã duyệt","entryCondition":"HOD duyệt"}],
          "included":true}]
        """;

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
    public void Select_AsksScreenScopeAfterFlowIsConfirmed()
    {
        Assert.Equal(InterviewTableKind.ScreenScope,
            InterviewTableGate.Select(ProjectWith(flowMap: ConfirmedFlow)));
    }

    [Fact]
    public void Select_AsksEntityMapAfterScreensAreConfirmed()
    {
        Assert.Equal(InterviewTableKind.EntityMap,
            InterviewTableGate.Select(ProjectWith(flowMap: ConfirmedFlow, screenScope: ConfirmedScreens)));
    }

    // Bảng phân quyền là cổng ÁP CHÓT. Dòng phân quyền chỉ [RÕ] khi bảng chốt, nên không có đường nào
    // soạn tài liệu mà bỏ qua ba bảng trước.
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
        var coverage = EverythingClear.Replace(
            "- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HOD từ chối thì Assistant sửa lại. {nguồn: \"trả về sửa lại\"}",
            "- Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
    }

    [Fact]
    public void FlowMapGate_StaysClosedWhileTheMainFlowIsStillOpen()
    {
        var coverage = EverythingClear.Replace(
            "- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý. {nguồn: \"Đúng luồng này\"}",
            "- ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: ai duyệt.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
    }

    // Các DÒNG của bảng là phạm vi đã chắt. Phạm vi trống thì bảng không có gì để hỏi — cùng luật với
    // bảng phân quyền.
    [Fact]
    public void ScreenScopeGate_StaysClosedWithoutAPlannedScope()
    {
        Assert.False(ScreenScopeGate.ShouldAsk(EverythingClear, null, new List<string>()));
    }

    // LẦN BÀY ĐẦU của bảng màn hình phải NHƯỜNG bảng luồng, và thứ tự ưu tiên ở Select KHÔNG đủ để bảo đảm
    // điều đó: nó chỉ phân xử được khi hai cổng cùng mở. Ca thật (dự án JD Libary 1): người dùng kể luồng
    // ngay lượt 3 nên «Chức năng & luồng nghiệp vụ chính» lên [RÕ] rất sớm, trong khi vai trò còn phải hỏi
    // thêm mấy lượt — cổng luồng ĐÓNG, cổng màn hình mở, và bảng màn hình bày ở lượt 12 còn bảng luồng mãi
    // lượt 20. Cột "màn này phục vụ bước nào" của lượt 12 ra rỗng vì chưa có bước nào tồn tại để gắn, nên
    // khi luồng chốt xong thì UncoveredActions báo gần như MỌI bước chưa ai phụ trách và người dùng phải rà
    // bảng màn hình lần thứ hai — chỉ vì lần đầu bày quá sớm.
    [Fact]
    public void ScreenScopeGate_StaysClosedUntilTheFlowGateCouldOpen()
    {
        var coverage = EverythingClear.Replace(
            "- ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant lập, HOD HR duyệt. {nguồn: \"Assistant lập, HOD duyệt\"}",
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: ai được xem.");

        Assert.False(FlowMapGate.ShouldAsk(coverage, null));
        Assert.False(ScreenScopeGate.ShouldAsk(coverage, null, Scope));
        // Và hệ quả phải thấy được ở đúng chỗ người dùng thấy: lượt này KHÔNG bày bảng màn hình.
        //
        // Cố ý KHÔNG khẳng định là `None`: bản đồ giả lập ở đây cho mọi nhóm khác [RÕ] nên cổng ĐỐI TƯỢNG
        // (chỉ đòi «Dữ liệu / danh mục chính» + «Vòng đời & trạng thái») vẫn mở và thắng lượt này. Đó là
        // hành vi có sẵn của EntityMapGate, không phải thứ test này chốt — xem ghi chú ở docs về khoảng hở
        // thứ tự còn lại của cổng đối tượng.
        Assert.NotEqual(InterviewTableKind.ScreenScope,
            InterviewTableGate.Select(ProjectWith(coverage: coverage)));
    }

    // Nửa còn lại của cùng một luật: đủ điều kiện rồi thì HAI cổng cùng mở, và Select mới là chỗ quyết định
    // bảng luồng đi trước. Điều kiện mới KHÔNG được siết tới mức đòi bảng luồng đã CHỐT — làm vậy là biến
    // một lượt bày bảng hỏng (model không trả nổi bảng dùng được) thành chuỗi kẹt vĩnh viễn.
    [Fact]
    public void ScreenScopeGate_OpensAlongsideTheFlowGate_NotAfterTheFlowTableIsConfirmed()
    {
        Assert.True(FlowMapGate.ShouldAsk(EverythingClear, null));
        Assert.True(ScreenScopeGate.ShouldAsk(EverythingClear, null, Scope));
        Assert.Equal(InterviewTableKind.FlowMap, InterviewTableGate.Select(ProjectWith()));
    }

    // BẤT BIẾN CHỐNG KHÓA CHÉO của chuỗi hai bảng cuối. Nhóm «Thông báo / nhắc nhở» không còn được hỏi
    // bằng câu hỏi nên nó nằm ở [CHƯA HỎI] suốt buổi; nếu cổng đối tượng vẫn đòi nhóm đó chạm tới thì cả
    // ba khóa nhau — bảng đối tượng chờ nhóm thông báo, nhóm thông báo chờ bảng thông báo, bảng thông báo
    // chờ bảng phân quyền (mà cổng phân quyền lại chờ bảng đối tượng qua chuỗi ưu tiên).
    [Fact]
    public void EntityMapGate_OpensWhileNotificationsAreStillUnasked()
    {
        var coverage = EverythingClear.Replace(
            "- Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit. {nguồn: \"gửi mail cho HOD\"}",
            "- Thông báo / nhắc nhở: [CHƯA HỎI]");

        Assert.True(EntityMapGate.ShouldAsk(coverage, null));
    }

    // Nửa còn lại của cùng bất biến: cổng phân quyền phải BỎ QUA dòng thông báo khi xét "cuối buổi", nếu
    // không nó đứng im chờ một nhóm chỉ [RÕ] sau bảng của chính nó — mà bảng ấy lại đứng sau bảng phân
    // quyền.
    [Fact]
    public void PermissionMatrixGate_OpensWhileNotificationsAreStillUnasked()
    {
        var coverage = EverythingClear.Replace(
            "- Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit. {nguồn: \"gửi mail cho HOD\"}",
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
