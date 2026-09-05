using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// NHỊP của lượt chắt lọc PHẠM VI MÀN HÌNH.
//
// Danh sách này từng là mục thứ ba của lượt "triển vọng phỏng vấn", nên nó chạy theo nhịp của lời gọi đó:
// sau MỖI lượt chat, kể từ lượt đầu tiên. Nhịp ấy sai ở hai đầu. Đầu rẻ là token — luật đặt tên màn hình
// cộng luật "chỉ màn hình, chức năng thì gộp vào màn chứa nó" chiếm hơn một phần ba prompt, và khối "bảng
// màn hình đang có" phải kể tới từng chức năng, tất cả đi theo ~35 lượt để phục vụ một hai lượt bày bảng.
// Đầu đắt là chất lượng: ở lượt 3 thì bảng luồng chưa chốt, bảng đối tượng chưa có, phạm vi chưa hình
// thành — màn hình model đoán ra lúc ấy là phỏng đoán sớm, mà Merge thì chỉ được phép THÊM. Dòng sai ấy
// nằm trong bảng cho tới khi chính người dùng bỏ tích nó, hai mươi lượt sau.
//
// Nhịp mới: im lặng tới sát cổng bảng màn hình, gộp bù trọn quãng, rồi theo LÔ cho phần phạm vi trôi tiếp.
//
// Và bản đồ ngã ngũ CHƯA phải là "sát cổng": bản đồ lên [RÕ] ngay khi hội thoại kể đủ, còn ba bảng đứng
// trước bảng màn hình (luồng → đối tượng → báo cáo) thì phải lần lượt bày ra và chờ người dùng bấm gửi.
// Ca thật (Safety Training 9): bản đồ ngã ngũ quanh lượt 40, bảng luồng bày ở lượt 44, bảng đối tượng ở
// lượt 46 — bốn lời gọi trong khoảng đó không lời nào dùng được, vì InterviewTableGate.Select còn đang
// nhường cho ba bảng kia nên bảng màn hình không có đường ra hỏi. Thứ ở lại: mười dòng chờ duyệt do model
// đoán trước khi bảng đối tượng chốt (Course List / Course Catalog / Course Management / Course Detail —
// một thứ bốn tên). Nên nhịp còn một vế nữa: BA BẢNG ĐỨNG TRƯỚC ĐÃ HẾT VIỆC.
public class InterviewScopeHarvestRhythmTests
{
    // Bản đồ của một dự án đã đi tới chỗ sắp bày bảng màn hình: mọi nhóm mà cổng đòi đều đã ngã ngũ.
    private static readonly string ReadyForTheScreenTable = CoverageMapFixture.Map("""
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

    // Đầu buổi: BA mới chạm tới bài toán, chưa nhóm nào chốt xong.
    private static readonly string EarlyInTheInterview = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [MỘT PHẦN] Muốn quản lý đào tạo. còn thiếu: phạm vi
        - ★ Đối tượng người dùng & vai trò: [CHƯA HỎI]
        - ★ Chức năng & luồng nghiệp vụ chính: [CHƯA HỎI]
        - Quy trình hiện tại & điểm khó: [CHƯA HỎI]
        - Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]
        - Dữ liệu / danh mục chính: [CHƯA HỎI]
        - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
        - Vòng đời & trạng thái: [CHƯA HỎI]
        - Thông báo / nhắc nhở: [CHƯA HỎI]
        - Báo cáo / thống kê: [CHƯA HỎI]
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        - Quy mô sử dụng: [CHƯA HỎI]
        """);

    // Ba bảng đứng trước, ở trạng thái ĐÃ CHỐT — "chốt" ở đây chỉ là "parse ra được ít nhất một dòng"
    // (FlowMapBuilder/EntityMapBuilder/ReportMapBuilder.IsConfirmed), nên fixture giữ đúng phần tối thiểu.
    private const string ConfirmedFlows = """
        [{"name":"Lập kế hoạch đào tạo","kind":"happy","role":"HR Assistant",
          "steps":[{"action":"Tạo bản kế hoạch quý","included":true}]}]
        """;

    private const string ConfirmedEntities = """
        [{"entity":"Kế hoạch đào tạo","fields":[],"states":[]}]
        """;

    private const string ConfirmedScreens = """
        [{"screen":"Training Plan Detail","purpose":"Lập kế hoạch",
          "functions":[{"name":"Tạo version plan","flowSteps":[],"included":true,"confirmedByUser":true}],
          "included":true,"confirmedByUser":true}]
        """;

    // ĐÂY LÀ CA NGƯỜI DÙNG BÁO: "mới lượt chat đầu tiên mà đã thấy nó chạy rồi". Ở lượt đó chưa có gì để
    // chắt — không bảng luồng, không bảng đối tượng, không phạm vi — nên mọi màn hình model nêu ra đều là
    // đoán, và đoán xong thì nằm lại trong bảng.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void StaysQuiet_WhileTheCoverageMapIsStillEarly(int turns)
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            EarlyInTheInterview, null, null, null, null, 0, turns));
    }

    // Tới sát cổng thì gộp bù TRỌN quãng đã qua trong một lời gọi — kể cả khi trước đó chưa gộp lượt nào.
    // "Sát cổng" = bản đồ ngã ngũ VÀ ba bảng đứng trước đã hết việc: ở fixture này bảng luồng và bảng đối
    // tượng đã chốt, còn cổng báo cáo không bao giờ mở vì nhóm của nó ở [KHÔNG ÁP DỤNG].
    [Fact]
    public void Harvests_WhenTheInterviewReachesTheScreenTableGate()
    {
        Assert.True(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, null, 0, 22));
    }

    // Trước lần chốt đầu KHÔNG có ngưỡng lô: quãng từ lúc bản đồ ngã ngũ tới lúc người dùng bấm gửi bảng
    // chỉ vài lượt, và lượt nào trong đó cũng có thể lộ ra màn hình mới. Hoãn chúng lại là bày ra một bảng
    // thiếu đúng phần vừa được nói tới — rồi người dùng đóng dấu "đây là toàn bộ màn hình" lên bảng ấy.
    [Fact]
    public void Harvests_EveryNewTurn_BeforeTheTableIsConfirmedOnce()
    {
        Assert.True(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, null, 22, 23));
    }

    // Sau lần chốt, phần phạm vi trôi tiếp KHÔNG còn gấp: bảng đã đứng, và một màn hình lộ muộn chỉ cần vào
    // kịp trước lúc sinh tài liệu. Chờ đủ lô để không trả tiền một lời gọi cho mỗi lượt gật đầu.
    [Fact]
    public void WaitsForABatch_AfterTheTableWasConfirmed()
    {
        for (var newTurns = 1; newTurns < InterviewScopeService.HarvestBatchThreshold; newTurns++)
        {
            Assert.False(InterviewScopeService.ShouldHarvest(
                ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, ConfirmedScreens,
                30, 30 + newTurns));
        }

        Assert.True(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, ConfirmedScreens,
            30, 30 + InterviewScopeService.HarvestBatchThreshold));
    }

    // Đủ lô vẫn chưa đủ: đường MỞ LẠI của cổng đòi nhóm «Chức năng & luồng nghiệp vụ chính» còn [RÕ], nên
    // lượt chắt lọc cũng chờ đúng điều kiện đó. Chắt ra một mục mà cổng không mở nổi để hỏi thì mục ấy chỉ
    // là một dòng chờ duyệt không ai rà.
    [Fact]
    public void StaysQuiet_AfterConfirmation_WhenTheMainFlowGroupSlippedBack()
    {
        var slipped = CoverageMapFixture.With(
            ReadyForTheScreenTable,
            "- ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] Có luồng duyệt. còn thiếu: ai duyệt bước hai");

        Assert.False(InterviewScopeService.ShouldHarvest(
            slipped, ConfirmedFlows, ConfirmedEntities, null, ConfirmedScreens,
            30, 30 + InterviewScopeService.HarvestBatchThreshold));
    }

    // CA NGƯỜI DÙNG BÁO LẦN HAI: bản đồ đã ngã ngũ từ lượt 40 nhưng bảng luồng mãi lượt 44 mới được bày.
    // Mọi lời gọi trong khoảng đó là tiền vứt đi — InterviewTableGate.Select đang nhường cho bảng luồng
    // nên bảng màn hình không có đường ra hỏi — và tệ hơn, thứ model đoán ra lúc chưa có bước luồng nào
    // thì ở lại trong bảng vĩnh viễn (Merge chỉ được THÊM).
    [Fact]
    public void StaysQuiet_WhileTheFlowTableIsStillWaitingToBeConfirmed()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, null, null, null, null, 0, 40));
    }

    // Cùng lý do, một bậc muộn hơn: bảng luồng đã chốt nhưng bảng ĐỐI TƯỢNG còn đang chờ người dùng gửi.
    // Đây là bảng gieo màn hình quản lý danh mục thẳng vào bảng màn hình (ConfirmEntityMapUseCase), nên
    // chắt trước lúc nó chốt là đoán đúng phần sắp được điền tất định — rồi thành hai dòng cho một màn.
    [Fact]
    public void StaysQuiet_WhileTheEntityTableIsStillWaitingToBeConfirmed()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, null, null, null, 0, 45));
    }

    // Bảng BÁO CÁO cũng vậy — cổng của nó chỉ mở khi bảng đối tượng đã chốt và nhóm «Báo cáo / thống kê»
    // đã [RÕ], và mỗi báo cáo còn giữ là một MÀN HÌNH gieo vào bảng màn hình.
    [Fact]
    public void StaysQuiet_WhileTheReportTableIsStillWaitingToBeConfirmed()
    {
        var reportsNeeded = CoverageMapFixture.With(
            ReadyForTheScreenTable,
            "- Báo cáo / thống kê: [RÕ] Cần báo cáo tỉ lệ hoàn thành theo phòng ban.");

        Assert.False(InterviewScopeService.ShouldHarvest(
            reportsNeeded, ConfirmedFlows, ConfirmedEntities, null, null, 0, 46));

        // Gửi bảng báo cáo xong thì không còn bảng nào đứng trước ⇒ gộp bù trọn quãng ngay lượt sau.
        Assert.True(InterviewScopeService.ShouldHarvest(
            reportsNeeded, ConfirmedFlows, ConfirmedEntities,
            """[{"report":"Tỉ lệ hoàn thành theo phòng ban","included":true}]""", null, 0, 46));
    }

    // Vế "ba bảng đứng trước" KHÔNG được biến thành một chỗ kẹt cho dự án không có danh mục nào: ở đó cổng
    // đối tượng không bao giờ mở (nhóm «Dữ liệu / danh mục chính» ở [KHÔNG ÁP DỤNG]) nên chờ nó CHỐT là xoá
    // luôn bảng màn hình khỏi buổi phỏng vấn. Cùng luật "ngã ngũ chứ không sẵn sàng" mà ScreenScopeGate áp.
    [Fact]
    public void Harvests_WhenAPrecedingTableWillNeverBeAsked()
    {
        var noCatalogs = CoverageMapFixture.With(
            ReadyForTheScreenTable,
            "- Dữ liệu / danh mục chính: [KHÔNG ÁP DỤNG] Ứng dụng không có danh mục nào.");

        Assert.True(InterviewScopeService.ShouldHarvest(
            noCatalogs, ConfirmedFlows, null, null, null, 0, 22));
    }

    // Không có lượt mới thì không có gì để đọc, dù mọi điều kiện khác đã đúng.
    [Fact]
    public void StaysQuiet_WithoutNewTurns()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, null, 22, 22));
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, ConfirmedEntities, null, ConfirmedScreens, 30, 30));
    }

    // Bản đồ chưa có dòng nào (dự án vừa mở) ⇒ không chắt. Fail-closed, cùng luật với các cổng bảng.
    [Fact]
    public void StaysQuiet_WithoutACoverageMap()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(null, null, null, null, null, 0, 5));
    }

    // Con trỏ RIÊNG: bản đọc từ entity phải soi đúng cột của lượt này, không phải con trỏ của lượt chắt
    // lọc bản đồ bao phủ. Dùng chung một con trỏ thì lượt chạy dày (bản đồ — mỗi lượt chat) kéo nó đi trước
    // và lượt chạy thưa này mất sạch quãng để gộp — đúng lỗi mà việc tách con trỏ sinh ra để chặn.
    [Fact]
    public void ReadsItsOwnPointer_NotTheCoveragePointer()
    {
        var project = new Project
        {
            RequirementCoverageMap = ReadyForTheScreenTable,
            FlowMap = ConfirmedFlows,
            EntityMap = ConfirmedEntities,
            CoverageHarvestedTurnCount = 22,
            InterviewScopeHarvestedTurnCount = 0
        };

        Assert.True(InterviewScopeService.ShouldHarvest(project, 22));
    }
}
