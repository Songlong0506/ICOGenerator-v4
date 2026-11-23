using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// NHỊP của lượt chắt lọc PHẠM VI TRÔI.
//
// Lượt này từng chắt TÊN MÀN HÌNH từ hội thoại, sớm và thường xuyên — và mỗi lần chắt sớm là một phỏng đoán ở
// lại vĩnh viễn (ca thật Safety Training 9: Course List / Course Catalog / Course Management / Course Detail —
// một thứ bốn tên). Nay lần thiết kế UX ĐẦU tự đọc trọn hội thoại, nên trước nó không cần lượt chắt nào. Lượt
// này chỉ còn một việc: SAU lần thiết kế đầu, theo LÔ, bắt các tác vụ mới người dùng kể thêm (phạm vi trôi)
// để cổng thiết kế mở lại. Ba vế phải giữ: chưa thiết kế ⇒ im; chưa đủ lô ⇒ im; luồng chính trượt khỏi [RÕ]
// hay bảng đứng trước chưa xong ⇒ im.
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

    // Các bảng đứng trước, ở trạng thái ĐÃ CHỐT — "chốt" ở đây chỉ là "parse ra được ít nhất một dòng"
    // (FlowMapBuilder/ReportMapBuilder.IsConfirmed), nên fixture giữ đúng phần tối thiểu.
    private const string ConfirmedFlows = """
        [{"name":"Lập kế hoạch đào tạo","kind":"happy","role":"HR Assistant",
          "steps":[{"action":"Tạo bản kế hoạch quý","included":true}]}]
        """;

    // Bản thiết kế UX đã có (lần thiết kế đầu đã chạy).
    private const string Designed = """
        {"version":1,"tasks":[{"id":"T-01","name":"Tạo version plan","placed":true}],
         "screens":[{"id":"S-01","name":"Training Plans","actions":[{"task":"T-01","label":"Tạo"}]}]}
        """;

    // ĐÂY LÀ CA NGƯỜI DÙNG BÁO: "mới lượt chat đầu tiên mà đã thấy nó chạy rồi". Ở lượt đó chưa có gì để
    // chắt — không bảng luồng, không bảng báo cáo, không phạm vi — nên mọi màn hình model nêu ra đều là
    // đoán, và đoán xong thì nằm lại trong bảng.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void StaysQuiet_WhileTheCoverageMapIsStillEarly(int turns)
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            EarlyInTheInterview, null, null, null, 0, turns));
    }

    // CHƯA thiết kế lần nào ⇒ im lặng, dù bản đồ đã ngã ngũ: lần thiết kế đầu tự đọc trọn hội thoại, nên một
    // lượt chắt chạy trước nó chỉ là một lời gọi thừa (và một bản đoán thứ hai chờ lệch).
    [Fact]
    public void StaysQuiet_BeforeTheFirstDesign()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, null, null, 0, 40));
    }

    // Sau lần thiết kế đầu, phần phạm vi trôi KHÔNG gấp: một tác vụ lộ muộn chỉ cần vào kịp trước lúc sinh tài
    // liệu. Chờ đủ lô để không trả tiền một lời gọi cho mỗi lượt gật đầu.
    [Fact]
    public void WaitsForABatch_AfterTheFirstDesign()
    {
        for (var newTurns = 1; newTurns < InterviewScopeService.HarvestBatchThreshold; newTurns++)
        {
            Assert.False(InterviewScopeService.ShouldHarvest(
                ReadyForTheScreenTable, ConfirmedFlows, null, Designed,
                30, 30 + newTurns));
        }

        Assert.True(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, null, Designed,
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
            slipped, ConfirmedFlows, null, Designed,
            30, 30 + InterviewScopeService.HarvestBatchThreshold));
    }

    // Bảng đứng trước (luồng, báo cáo) còn chờ ⇒ im: chúng là nguồn tác vụ, và chắt trước lúc chúng xong là
    // đoán đúng phần sắp được điền tất định.
    [Fact]
    public void StaysQuiet_WhileAPrecedingTableIsStillWaiting()
    {
        var reportsNeeded = CoverageMapFixture.With(
            ReadyForTheScreenTable,
            "- Báo cáo / thống kê: [RÕ] Cần báo cáo tỉ lệ hoàn thành theo phòng ban.");

        Assert.False(InterviewScopeService.ShouldHarvest(
            reportsNeeded, ConfirmedFlows, null, Designed, 30, 30 + InterviewScopeService.HarvestBatchThreshold));

        Assert.True(InterviewScopeService.ShouldHarvest(
            reportsNeeded, ConfirmedFlows,
            """[{"report":"Tỉ lệ hoàn thành theo phòng ban","included":true}]""", Designed,
            30, 30 + InterviewScopeService.HarvestBatchThreshold));
    }

    // Không có lượt mới thì không có gì để đọc, dù mọi điều kiện khác đã đúng.
    [Fact]
    public void StaysQuiet_WithoutNewTurns()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, null, Designed, 22, 22));
        Assert.False(InterviewScopeService.ShouldHarvest(
            ReadyForTheScreenTable, ConfirmedFlows, null, Designed, 30, 30));
    }

    // Bản đồ chưa có dòng nào (dự án vừa mở) ⇒ không chắt. Fail-closed, cùng luật với các cổng bảng.
    [Fact]
    public void StaysQuiet_WithoutACoverageMap()
    {
        Assert.False(InterviewScopeService.ShouldHarvest(null, null, null, null, 0, 5));
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
            UxBlueprint = Designed,
            CoverageHarvestedTurnCount = 22 + InterviewScopeService.HarvestBatchThreshold,
            InterviewScopeHarvestedTurnCount = 22
        };

        Assert.True(InterviewScopeService.ShouldHarvest(project, 22 + InterviewScopeService.HarvestBatchThreshold));
    }
}
