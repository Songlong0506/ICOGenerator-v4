using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng chọn ĐÚNG MỘT bảng cho mỗi lượt chat. Ba điều phải giữ bằng test, và cả ba là những chỗ mà một lần
// sửa vô ý sẽ làm hỏng cả buổi phỏng vấn chứ không chỉ một lượt:
//
//  1. THỨ TỰ là thứ tự phụ thuộc — luồng → màn hình → đối tượng → phân quyền. Bảng màn hình có ô "màn này
//     phục vụ bước nào", và các DÒNG của bảng phân quyền chính là màn hình. Hỏi ngược là bày ra một bảng
//     mà chính BA cũng chưa đủ dữ kiện để điền sẵn.
//  2. KHÔNG BAO GIỜ hai bảng cùng lượt: hai khối "## LƯỢT NÀY:" là hai mệnh lệnh chọi nhau.
//  3. KHÔNG KHÓA CHÉO. Đây là cái bẫy đắt nhất: PermissionMatrixGate phải bỏ qua chính dòng phân quyền vì
//     dòng đó chỉ [RÕ] sau khi bảng chốt. Nếu ba bảng mới cũng được cho luật "chưa có bảng ⇒ không bao
//     giờ [RÕ]" thì cổng (đòi nhóm [RÕ]) và bản đồ (đòi có bảng) khóa chặt nhau. Test dưới đây chốt rằng
//     khi cổng phân quyền mở thì cả ba cổng kia cũng mở được — tức chúng luôn được hỏi TRƯỚC nó.
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
        string? permissionMatrix = null)
        => new()
        {
            RequirementCoverageMap = coverage,
            // Cột PlannedScope lưu dạng bullet ("- …") — InterviewOutlookService.ParseItems bỏ mọi dòng
            // không có tiền tố đó, nên viết thẳng tên màn hình vào đây là dựng một dự án có phạm vi RỖNG.
            PlannedScope = string.Join("\n", Scope.Select(s => "- " + s)),
            FlowMap = flowMap,
            ScreenScopeMap = screenScope,
            EntityMap = entityMap,
            PermissionMatrix = permissionMatrix
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
          "states":[{"state":"Nháp","entryCondition":"vừa tạo","notify":""},
                    {"state":"Đã duyệt","entryCondition":"HOD duyệt","notify":"HR Assistant"}],
          "included":true}]
        """;

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

    // Bảng phân quyền là cổng CUỐI CÙNG — nó cũng là cổng duy nhất mở nút "Write Requirement" (dòng phân
    // quyền chỉ [RÕ] khi bảng chốt), nên không có đường nào soạn tài liệu mà bỏ qua ba bảng trước.
    [Fact]
    public void Select_AsksPermissionMatrixLast()
    {
        Assert.Equal(InterviewTableKind.PermissionMatrix,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens, entityMap: ConfirmedEntities)));
    }

    [Fact]
    public void Select_ReturnsNoneWhenEveryTableIsConfirmed()
    {
        Assert.Equal(InterviewTableKind.None,
            InterviewTableGate.Select(ProjectWith(
                flowMap: ConfirmedFlow, screenScope: ConfirmedScreens, entityMap: ConfirmedEntities,
                permissionMatrix: """[{"screen":"Màn hình Training Plan","function":"Xem","grants":[{"role":"HOD HR","scope":"tất cả"}]}]""")));
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

    // Bảng đối tượng có cột "báo cho ai" ở từng chuyển trạng thái — chưa ai chạm tới nhóm thông báo thì cả
    // cột đó là phỏng đoán.
    [Fact]
    public void EntityMapGate_StaysClosedWhileNotificationsAreUnasked()
    {
        var coverage = EverythingClear.Replace(
            "- Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit. {nguồn: \"gửi mail cho HOD\"}",
            "- Thông báo / nhắc nhở: [CHƯA HỎI]");

        Assert.False(EntityMapGate.ShouldAsk(coverage, null));
    }

    // Nhóm bị đánh [KHÔNG ÁP DỤNG] là đã CHẠM TỚI, không phải còn treo: dự án không gửi thông báo cho ai
    // vẫn phải chốt được bảng đối tượng, nếu không cổng đứng im vĩnh viễn.
    [Fact]
    public void EntityMapGate_TreatsNotApplicableAsSettled()
    {
        var coverage = EverythingClear.Replace(
            "- Thông báo / nhắc nhở: [RÕ] Báo HOD khi submit. {nguồn: \"gửi mail cho HOD\"}",
            "- Thông báo / nhắc nhở: [KHÔNG ÁP DỤNG] Không gửi thông báo. {nguồn: \"không cần báo ai\"}");

        Assert.True(EntityMapGate.ShouldAsk(coverage, null));
    }

    // Bản đồ trống (dự án vừa tạo, hoặc vừa "New Chat") ⇒ mọi cổng đóng. Fail-closed: bày một bảng dựng
    // trên hư không là cách nhanh nhất để thu về một chữ ký cho phán đoán của BA.
    [Fact]
    public void Select_ReturnsNoneWithoutACoverageMap()
    {
        Assert.Equal(InterviewTableKind.None, InterviewTableGate.Select(ProjectWith(coverage: "")));
    }
}
