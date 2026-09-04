using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BẤT BIẾN TRUNG TÂM phía yêu cầu: một nhóm còn câu hỏi MỞ thì dòng bản đồ của nó không được [RÕ].
//
// Ca thật (dự án Learning and Development 7, 42 lượt): bản đồ ghi «Luồng ngoại lệ & trường hợp đặc biệt»,
// «Vòng đời & trạng thái» và «Dữ liệu / danh mục chính» đều [RÕ], trong khi cùng lúc đó hệ thống đang giữ
// bảy câu hỏi thuộc đúng ba nhóm ấy:
//
//   - "Chưa rõ nhân viên có đăng ký lại được sau khi ticket bị Reject hay không"      → Luồng ngoại lệ
//   - "Chưa rõ kết quả Complete/Not Complete/No Show dùng để xử lý bước nào tiếp theo" → Vòng đời & trạng thái
//   - "Chưa rõ xử lý khi Item ID và Item Title không tạo thành cặp mã–tên duy nhất"    → Dữ liệu / danh mục
//
// [RÕ] không phải một nhãn trạng thái mà là một LỆNH CẤM: requirement-chat.v4.md cấm BA hỏi lại nhóm đã
// [RÕ]. Nên bảy câu đó vĩnh viễn không bao giờ được hỏi, và bước soạn tài liệu — vốn bị cấm giả định —
// nhận một khoảng trống mà không cổng nào báo.
//
// Từ khi bản đồ và danh sách câu hỏi ra đời trong CÙNG một lời gọi, guard này không còn phải hoà giải hai
// nhịp: nó chỉ áp bất biến trên một tài liệu tự mâu thuẫn.
public class CoveragePendingGuardTests
{
    private static (List<CoverageMapItem> Items, List<OpenQuestionEntry> Questions) Apply(
        string bullets, params string[] questionLines)
    {
        var items = CoverageMapFixture.Items(bullets).ToList();
        var questions = OpenQuestionFixture.Items(questionLines).ToList();
        CoveragePendingGuard.Apply(items, questions);
        return (items, questions);
    }

    private static CoverageMapItem Row(IEnumerable<CoverageMapItem> items, string labelPrefix) =>
        items.First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    [Fact]
    public void ClearRow_IsDowngraded_WhenItsGroupStillHasAnOpenQuestion()
    {
        var (items, questions) = Apply("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học cả năm. {nguồn: "lên kế hoạch các lớp học"}
            - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì ticket sang Waitlist. {nguồn: "Tiếp tục giữ Waitlist"}
            """,
            "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ nhân viên có đăng ký lại được sau khi ticket bị Reject hay không");

        var exception = Row(items, "Luồng ngoại lệ");
        Assert.Equal("MỘT PHẦN", exception.Status);
        // Phần đã ghi nhận và bằng chứng của dòng giữ NGUYÊN: chúng là căn cứ cho điều đã biết, không phải
        // cho phần còn thiếu, và xoá đi là làm panel tiến độ mất lý do vì sao nhóm này từng được chấm [RÕ].
        Assert.Equal("Lớp đầy thì ticket sang Waitlist.", exception.Known);
        Assert.Equal("\"Tiếp tục giữ Waitlist\"", exception.Evidence);
        // …còn dòng không liên quan thì không bị đụng tới.
        Assert.Equal("RÕ", Row(items, "Mục tiêu").Status);
        // Danh sách câu hỏi chỉ được ĐỌC: quyền xoá một câu thuộc về các guard đứng trước.
        Assert.Single(questions);
    }

    // Hạ dòng không phải mục đích cuối: mục đích là câu hỏi ấy được ĐEM RA HỎI. Cổng readiness chỉ chọn
    // trong các dòng [MỘT PHẦN]/[CHƯA HỎI], nên một dòng [RÕ] oan là một câu hỏi vĩnh viễn không tới lượt.
    [Fact]
    public void TheDowngradedRow_BecomesTheQuestionTheGateAsks()
    {
        var (items, questions) = Apply(
            "- Vòng đời & trạng thái: [RÕ] Ticket đi Pending → Enroll/Waitlist → Complete. {nguồn: bảng luồng đã chốt}",
            "[Vòng đời & trạng thái] Chưa rõ kết quả Complete/Not Complete/No Show được dùng để xử lý bước nào tiếp theo");

        var readiness = RequirementReadinessGate.Evaluate(CoverageMapParser.Serialize(items), questions);

        Assert.False(readiness.Ready);
        Assert.Contains("kết quả Complete/Not Complete/No Show được dùng để xử lý bước nào tiếp theo",
            readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Lượt chắt lọc viết "Luồng ngoại lệ" còn bản đồ ghi "Luồng ngoại lệ & trường hợp đặc biệt" — vẫn là
    // một nhóm. So khớp nguyên văn ở đây là để guard câm trong im lặng, cùng lý do mà InterviewTableGate
    // và PermissionMatrixGate đều so bằng tiền tố.
    [Theory]
    [InlineData("Luồng ngoại lệ")]
    [InlineData("Luồng ngoại lệ & trường hợp đặc biệt")]
    public void TheGroup_MatchesTheMapLabelByPrefix_InBothDirections(string tag)
    {
        var (items, _) = Apply(
            "- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì Waitlist.",
            $"[{tag}] Chưa rõ ticket Waitlist còn treo khi lớp đã kết thúc");

        Assert.Equal("MỘT PHẦN", Row(items, "Luồng ngoại lệ").Status);
    }

    // Guard chạy MỘT CHIỀU. Hạ nhầm thì BA hỏi thêm một câu; nâng nhầm thì sinh ra một khoảng trống mà mọi
    // tầng sau tin là đã đủ — hai cái giá không cùng hạng, nên nó không bao giờ được nâng cấp hộ distiller.
    [Fact]
    public void Guard_NeverUpgrades_AndNeverTouchesOtherStatuses()
    {
        var (items, _) = Apply("""
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Người dùng nói không cần báo cáo.
            - Quy mô sử dụng: [MỘT PHẦN] Toàn nhà máy.
            """,
            "[Thông báo / nhắc nhở] Chưa rõ ai nhận email khi ticket chờ duyệt",
            "[Báo cáo / thống kê] Chưa rõ cấp quản lý cần xem báo cáo nào",
            "[Quy mô sử dụng] Chưa rõ mỗi năm mở bao nhiêu lớp");

        Assert.Equal("CHƯA HỎI", Row(items, "Thông báo").Status);
        Assert.Equal("KHÔNG ÁP DỤNG", Row(items, "Báo cáo").Status);
        Assert.Equal("MỘT PHẦN", Row(items, "Quy mô").Status);
    }

    // Câu hỏi ĐÃ TRẢ LỜI không hạ dòng nào: nó ở lại danh sách chỉ để lượt chắt lọc kế tiếp không dựng lại
    // đúng câu ấy. Đọc nó thành một điểm còn treo là khoá cổng bằng chính câu người dùng vừa trả lời — và
    // vì mục đã trả lời thì KHÔNG BAO GIỜ rời danh sách, cổng sẽ khoá vĩnh viễn.
    [Fact]
    public void AnAnsweredQuestion_NeverDowngradesAnything()
    {
        var items = CoverageMapFixture.Items("- Vòng đời & trạng thái: [RÕ] Ticket đi Pending → Enroll → Complete.").ToList();
        var questions = new List<OpenQuestionEntry>
        {
            OpenQuestionFixture.Answered("[Vòng đời & trạng thái] Chưa rõ trạng thái sau Complete", "sau Complete là đóng hồ sơ")
        };

        CoveragePendingGuard.Apply(items, questions);

        Assert.Equal("RÕ", Assert.Single(items).Status);
    }

    // Nhóm model tự nghĩ ra (không khớp nhãn nào) và mục không có nhóm đều bị BỎ QUA: guard fail-open, nó
    // không được phép hạ nhầm một dòng vì một cái nhãn vô nghĩa. Đường ghi đã xoá nhãn lạ về rỗng
    // (RequirementCoverageService.Canonicalize), nhưng guard vẫn phải tự đứng vững trước cả hai ca.
    [Theory]
    [InlineData("[Tích hợp hệ thống ngoài] Chưa rõ nối với SAP kiểu gì")]
    [InlineData("[—] Chưa rõ một điểm không thuộc nhóm nào")]
    [InlineData("Chưa rõ điểm này thuộc nhóm nào — mục không có nhóm")]
    public void UnknownOrMissingGroup_LeavesTheMapAlone(string question)
    {
        var (items, _) = Apply("- Vòng đời & trạng thái: [RÕ] Ticket đi Pending → Enroll → Complete.", question);

        Assert.Equal("RÕ", Assert.Single(items).Status);
    }

    // Nhiều câu hỏi cùng một nhóm ⇒ dòng vẫn chỉ bị hạ MỘT lần, và mọi câu ở lại danh sách. Cổng hỏi mỗi
    // lượt một câu (xem RequirementReadinessGate), nên các câu còn lại không mất đi đâu — chúng tới lượt ở
    // vòng sau, thay vì bị gộp thành một câu hỏi dồn mà người dùng chỉ trả lời được vế đầu.
    [Fact]
    public void ManyQuestionsInOneGroup_AllSurvive_AndTheRowIsDowngradedOnce()
    {
        var (items, questions) = Apply(
            "- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì Waitlist.",
            "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ đăng ký lại sau khi bị Reject",
            "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ đăng ký trùng lịch");

        Assert.Equal("MỘT PHẦN", Assert.Single(items).Status);
        Assert.Equal(2, questions.Count);

        var readiness = RequirementReadinessGate.Evaluate(CoverageMapParser.Serialize(items), questions);
        Assert.Contains("đăng ký lại sau khi bị Reject", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("trùng lịch", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoQuestions_LeavesTheMapUntouched()
    {
        var (items, _) = Apply("- ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học.");

        Assert.Equal("RÕ", Assert.Single(items).Status);

        // Bản đồ rỗng cũng không được làm guard ngã.
        CoveragePendingGuard.Apply(
            Array.Empty<CoverageMapItem>(),
            OpenQuestionFixture.Items("[Vòng đời & trạng thái] Chưa rõ gì đó"));
    }
}
