using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Danh sách câu hỏi không được mang một "câu hỏi" mà người dùng đọc lên không biết trả lời gì.
//
// Ca thật (dự án quản lý khóa học bắt buộc): nhóm «Thông báo / nhắc nhở» đứng [MỘT PHẦN] với câu hỏi
// "Bảng thông báo theo sự kiện chưa được chốt." — một câu MÔ TẢ TRẠNG THÁI HỆ THỐNG. Cổng
// "Write Requirement" phát nguyên văn nó ra khung chat, người dùng không có cách nào trả lời, mà chính BA
// đọc dòng đó cũng không biết phải hỏi gì. Và nhóm ấy là nhóm CHỐT BẰNG BẢNG: BA bị cấm hỏi lẻ nó, nên
// không lượt chat nào đóng câu hỏi đó lại được — đường đúng là bày bảng thông báo ra.
//
// Luật cũ chỉ nằm trong prompt (lời dặn cho model) và trong một phép thử ở ĐƯỜNG ĐỌC của cổng readiness.
// Guard này đưa phép thử về ĐƯỜNG GHI để mọi tầng — ngữ cảnh chat của BA, panel tiến độ, lượt distill kế
// tiếp — thấy cùng một sự thật.
public class CoverageQuestionGuardTests
{
    // Guard chỉ đụng tới DANH SÁCH CÂU HỎI: nhóm của mỗi mục là thứ duy nhất nó cần, nên nó không nhận
    // bản đồ. Fixture vẫn viết một dòng bullet vì đó là cách đọc tự nhiên nhất — nhóm + trạng thái + câu hỏi.
    private static List<OpenQuestionEntry> Apply(string bullets)
    {
        var questions = CoverageMapFixture.Questions(bullets);
        CoverageQuestionGuard.Apply(questions);
        return questions;
    }

    // Ca thật, nguyên văn.
    [Fact]
    public void AStateReport_IsNotAQuestion_AndIsDropped()
    {
        const string bullets = "- Vòng đời & trạng thái: [MỘT PHẦN] Chưa học / Đã đăng ký / Hoàn thành / Hết hạn. "
            + "còn thiếu: Bảng trạng thái chưa được chốt {nguồn: \"có thêm trạng thái Đóng\"}";

        Assert.Empty(Apply(bullets));
        // Chỉ xoá câu hỏi — bản đồ không phải việc của guard này, nó còn không nhìn thấy bản đồ.
        var row = Assert.Single(CoverageMapFixture.Items(bullets));
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.Equal("Chưa học / Đã đăng ký / Hoàn thành / Hết hạn.", row.Known);
    }

    // Cụm "chưa rõ" ở ĐẦU câu là cách viết câu hỏi bình thường của distiller — lưới bắt theo ĐUÔI câu nên
    // nó không đụng tới. Đây là ranh giới đắt nhất của guard: bắt rộng một chút là xoá mất câu hỏi thật.
    [Theory]
    [InlineData("chưa rõ ai duyệt đơn thay cho trưởng phòng")]
    [InlineData("chưa chốt cách tính điểm cuối kỳ từ điểm thành phần")]
    [InlineData("mỗi kết quả Complete / Not Complete / No Show thì hồ sơ chuyển sang bước nào")]
    [InlineData("cách xử lý khi nhân viên chuyển phòng ban và khi khóa học bị hủy")]
    public void ARealQuestion_Survives(string question)
    {
        var kept = Apply($"- Vòng đời & trạng thái: [MỘT PHẦN] Đã có 4 trạng thái. còn thiếu: {question}");

        Assert.Equal(question, Assert.Single(kept).Text);
    }

    [Theory]
    [InlineData("Danh sách vai trò chưa xác định")]
    [InlineData("Bộ cột chính thức chưa được xác nhận")]
    [InlineData("Cách tính điểm chưa rõ")]
    public void EveryStateReportTail_IsDropped(string report)
    {
        Assert.Empty(Apply($"- Vòng đời & trạng thái: [MỘT PHẦN] Đã có 4 trạng thái. còn thiếu: {report}"));
    }

    // Mẩu rỗng nghĩa: luật cũ của RequirementReadinessGate, nay chạy luôn ở đường ghi nên nó không còn
    // sống sót trong DB để đi vào ngữ cảnh chat của BA ở mọi lượt sau.
    [Fact]
    public void AHollowQuestion_IsDropped()
    {
        Assert.Empty(Apply("- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mã JD duy nhất. còn thiếu: các quy tắc khác (nếu có)"));
    }

    // Hai nhóm chốt-bằng-bảng: câu hỏi có hay tới đâu cũng là câu hỏi CHẾT, vì BA bị cấm hỏi lẻ chúng.
    [Theory]
    [InlineData("Thông báo / nhắc nhở", "ai nhận email khi khóa học sắp hết hạn")]
    [InlineData("Phân quyền theo nghiệp vụ", "vai trò nào được sửa danh mục khóa học")]
    public void AQuestionOnATableDecidedGroup_IsDropped(string label, string question)
    {
        Assert.Empty(Apply($"- {label}: [MỘT PHẦN] Đã bàn sơ bộ. còn thiếu: {question}"));
    }

    // Người dùng vừa nói BA hiểu sai nhóm này ⇒ đường hỏi lại mà họ tự mở ra phải sống, kể cả ở hai nhóm
    // chốt-bằng-bảng. Cụm đánh dấu còn là TÍN HIỆU MÁY: AskedQuestionHistory.ReopenedGroups đọc nó để miễn
    // phanh chống-hỏi-lại, nên xoá ô này là cướp mất cái đường ấy trong im lặng.
    [Fact]
    public void ARowTheUserJustReopened_IsLeftAlone()
    {
        var reopened = $"{AskedQuestionHistory.ReopenNote} — cần hỏi lại và chốt lại. Bảng thông báo chưa được chốt";
        var kept = Apply($"- Thông báo / nhắc nhở: [MỘT PHẦN] Email khi sắp hết hạn. còn thiếu: {reopened}");

        Assert.Equal(reopened, Assert.Single(kept).Text);
    }

    // Không có gì để xoá ⇒ danh sách nguyên vẹn: RequirementCoverageService so chuỗi đã serialize để khỏi
    // ghi DB mỗi lượt chat.
    [Fact]
    public void AListWithNothingToDrop_IsLeftAlone()
    {
        var kept = Apply("- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý khóa học bắt buộc. còn thiếu: khóa nào là bắt buộc với ai");

        Assert.Equal("khóa nào là bắt buộc với ai", Assert.Single(kept).Text);
    }

    // Mục ĐÃ TRẢ LỜI đứng ngoài mọi phép xoá: nó không còn là câu hỏi, và nó phải ở lại danh sách để lượt
    // chắt lọc kế tiếp không dựng lại đúng câu ấy.
    [Fact]
    public void AnAnsweredQuestion_IsNeverDropped_EvenIfItReadsLikeAStateReport()
    {
        var questions = new List<OpenQuestionEntry>
        {
            OpenQuestionFixture.Answered("[Vòng đời & trạng thái] Bảng trạng thái chưa được chốt", "đã chốt 4 trạng thái")
        };

        CoverageQuestionGuard.Apply(questions);

        Assert.Single(questions);
    }

    // Triệu chứng người dùng thật sự thấy: cổng thôi phát một câu không trả lời được, và chuyển sang câu
    // phát lại — thứ đóng lại được bằng một lượt.
    [Fact]
    public void TheGate_AsksAClosableQuestion_InsteadOfTheDeadOne()
    {
        const string bullets = "- ★ Mục tiêu / bài toán: [RÕ] Quản lý khóa học bắt buộc. {nguồn: \"quản lý việc học các khóa bắt buộc\"}\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] Email nhắc trước 30 ngày. còn thiếu: Bảng thông báo theo sự kiện chưa được chốt";

        var gate = RequirementReadinessGate.Evaluate(CoverageMapFixture.Map(bullets), CoverageMapFixture.Questions(bullets));

        Assert.False(gate.Ready);
        Assert.DoesNotContain("chưa được chốt", gate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Email nhắc trước 30 ngày", gate.Message, StringComparison.Ordinal);
    }
}
