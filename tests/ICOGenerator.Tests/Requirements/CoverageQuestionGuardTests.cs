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
// Guard chạy ở ĐƯỜNG GHI để mọi tầng — ngữ cảnh chat của BA, panel tiến độ, lượt distill kế tiếp — thấy
// cùng một sự thật. Nó có HAI nguồn loại câu hỏi: phần HÌNH DẠNG tự làm (câu rỗng, nhóm chốt-bằng-bảng),
// phần NGHĨA nhận từ lượt trọng tài (InterviewJudge) qua tham số `rejected`. File này ghim phần hình dạng
// và cách guard THI HÀNH một phán quyết; bản thân phán quyết được ghim ở InterviewJudgeTests.
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

    // Câu trọng tài loại thì bị xoá — và CHỈ câu hỏi bị xoá: bản đồ không phải việc của guard này, nó còn
    // không nhìn thấy bản đồ. Ca thật, nguyên văn: một câu tường thuật trạng thái lên tới màn hình.
    [Fact]
    public void AQuestionTheJudgeRejected_IsDropped_AndOnlyTheQuestion()
    {
        const string bullets = "- Vòng đời & trạng thái: [MỘT PHẦN] Chưa học / Đã đăng ký / Hoàn thành / Hết hạn. "
            + "còn thiếu: Bảng trạng thái chưa được chốt";
        var questions = CoverageMapFixture.Questions(bullets);

        CoverageQuestionGuard.Apply(questions, rejected: questions.ToList());

        Assert.Empty(questions);
        var row = Assert.Single(CoverageMapFixture.Items(bullets));
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.Equal(new[] { "Chưa học / Đã đăng ký / Hoàn thành / Hết hạn." }, row.Known);
    }

    // KHÔNG có phán quyết (trọng tài lỗi, hoặc đường sửa chữa không gọi model) ⇒ guard KHÔNG tự đoán nghĩa.
    // Ở đây từng có hai bảng cụm tiếng Việt ("…chưa được chốt", "các … khác (nếu có)") xoá những câu này;
    // chúng đã bị gỡ, nên một câu như thế nay sống sót cho tới khi trọng tài loại nó. Fail-open có chủ ý.
    [Theory]
    [InlineData("Bảng trạng thái chưa được chốt")]
    [InlineData("các quy tắc khác (nếu có)")]
    [InlineData("Notification rules not confirmed yet")]
    public void WithoutAVerdict_TheGuardDoesNotGuessMeaning(string question)
    {
        var kept = Apply($"- Vòng đời & trạng thái: [MỘT PHẦN] Đã có 4 trạng thái. còn thiếu: {question}");

        Assert.Equal(question, Assert.Single(kept).Text);
    }

    // Phán quyết so theo THAM CHIẾU: một mục khác có cùng nội dung không bị xoá theo.
    [Fact]
    public void AVerdict_OnlyRemovesTheVeryEntriesItNames()
    {
        var questions = CoverageMapFixture.Questions(
            "- Vòng đời & trạng thái: [MỘT PHẦN] Đã có 4 trạng thái. còn thiếu: điều kiện chuyển từ Đã đăng ký sang Hoàn thành\n"
            + "- Quy mô sử dụng: [MỘT PHẦN] Một nhà máy. còn thiếu: áng chừng bao nhiêu người dùng");

        CoverageQuestionGuard.Apply(questions, rejected: new[] { questions[0] });

        Assert.Equal("áng chừng bao nhiêu người dùng", Assert.Single(questions).Text);
    }

    // Mục chỉ được đem cho trọng tài khi guard CÓ QUYỀN xoá nó và chưa tự xoá nó: còn mở, không mang cụm
    // tái mở, không thuộc nhóm chốt-bằng-bảng.
    [Fact]
    public void IsJudgeable_OnlyOffersWhatTheGuardMayActuallyDrop()
    {
        Assert.True(CoverageQuestionGuard.IsJudgeable(
            new OpenQuestionEntry { Group = "Vòng đời & trạng thái", Text = "điều kiện chuyển trạng thái" }));
        Assert.False(CoverageQuestionGuard.IsJudgeable(
            new OpenQuestionEntry { Group = "Thông báo / nhắc nhở", Text = "ai nhận email" }));
        Assert.False(CoverageQuestionGuard.IsJudgeable(
            new OpenQuestionEntry { Group = "Vòng đời & trạng thái", Text = $"{AskedQuestionHistory.ReopenNote}. Hỏi lại các trạng thái" }));
        Assert.False(CoverageQuestionGuard.IsJudgeable(
            new OpenQuestionEntry { Group = "Vòng đời & trạng thái", Text = "x", Status = OpenQuestionEntry.Answered, Answer = "y" }));
    }

    // Câu hỏi thật không bị guard tự ý đụng tới, dù mở đầu bằng "chưa rõ" hay kết bằng chữ gì.
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
        var kept = CoverageMapFixture.Questions($"- Thông báo / nhắc nhở: [MỘT PHẦN] Email khi sắp hết hạn. còn thiếu: {reopened}");

        // Kể cả khi một phán quyết gọi tên nó: cụm tái mở đứng ngoài mọi phép xoá.
        CoverageQuestionGuard.Apply(kept, rejected: kept.ToList());

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
    // chắt lọc kế tiếp không dựng lại đúng câu ấy — kể cả khi một phán quyết lỡ gọi tên nó.
    [Fact]
    public void AnAnsweredQuestion_IsNeverDropped_EvenIfAVerdictNamesIt()
    {
        var questions = new List<OpenQuestionEntry>
        {
            OpenQuestionFixture.Answered("[Vòng đời & trạng thái] Bảng trạng thái chưa được chốt", "đã chốt 4 trạng thái")
        };

        CoverageQuestionGuard.Apply(questions, rejected: questions.ToList());

        Assert.Single(questions);
    }

    // Triệu chứng người dùng thật sự thấy: cổng thôi phát một câu không trả lời được, và chuyển sang câu
    // phát lại — thứ đóng lại được bằng một lượt. Ở đường ĐỌC cổng chỉ tự vệ bằng phần hình dạng (ở đây:
    // nhóm chốt-bằng-bảng); phần nghĩa đã được lọc ở đường ghi.
    [Fact]
    public void TheGate_AsksAClosableQuestion_InsteadOfTheDeadOne()
    {
        const string bullets = "- ★ Mục tiêu / bài toán: [RÕ] Quản lý khóa học bắt buộc.\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] Email nhắc trước 30 ngày. còn thiếu: Bảng thông báo theo sự kiện chưa được chốt";

        var gate = RequirementReadinessGate.Evaluate(CoverageMapFixture.Map(bullets), CoverageMapFixture.Questions(bullets));

        Assert.False(gate.Ready);
        Assert.DoesNotContain("chưa được chốt", gate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Email nhắc trước 30 ngày", gate.Message, StringComparison.Ordinal);
    }
}
