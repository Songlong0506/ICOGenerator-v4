using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// QUY TẮC CHỞ CON SỐ mà chưa có ví dụ tính thử nào thì không được [RÕ].
//
// Công thức hiểu sai là lỗi mà không cổng nào phía sau bắt được: các cổng đều hỏi "có thông tin chưa",
// không hỏi "thông tin đó có đúng không". Tài liệu sẽ ghi đúng… điều đã hiểu sai, rồi spec và POC sai
// theo. Thứ duy nhất bắt được là một ví dụ số người dùng đã xác nhận — nó vừa là bằng chứng hiểu đúng,
// vừa là oracle mà bản demo bị chấm theo (PocWorkedExampleOracle).
//
// Ca thật (dự án JD Libary 5, lượt 13): người dùng nêu "Responsibility (5 cái và có %, và có 1 item mặc
// định không được sửa là «Other task assign by manager» % từ 5-10)". BA ghi nhận nguyên văn rồi đi tiếp;
// dòng «Quy tắc nghiệp vụ & ràng buộc» chở đủ các con số ấy, mục "Ví dụ đã xác nhận" trống trơn suốt
// buổi. Ba câu không ai trả lời: 5 là cố định hay tối thiểu, tổng % có bằng 100 không, và khoảng 5–10 là
// của riêng dòng mặc định hay của mọi dòng.
public class CoverageWorkedExampleGuardTests
{
    // Guard nhận cả hai nửa: dòng bản đồ (để hạ trạng thái) và danh sách câu hỏi (để THÊM câu xin ví dụ).
    private static (List<CoverageMapItem> Items, List<OpenQuestionEntry> Questions) Apply(
        string bullets, params string[] workedExamples)
    {
        var items = CoverageMapFixture.Items(bullets).ToList();
        var questions = CoverageMapFixture.Questions(bullets);
        CoverageWorkedExampleGuard.Apply(items, questions, workedExamples);
        return (items, questions);
    }

    private static CoverageMapItem Row(IEnumerable<CoverageMapItem> items, string labelPrefix) =>
        items.First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    private const string RuleRowWithNumbers =
        "- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Responsibility có 5 cái kèm %, 1 item mặc định % từ 5-10. "
        + "";

    [Fact]
    public void ARuleRowCarryingNumbers_IsDowngraded_WhenNoWorkedExampleIsConfirmed()
    {
        var (items, questions) = Apply(RuleRowWithNumbers);

        var row = Row(items, "Quy tắc nghiệp vụ");
        Assert.Equal("MỘT PHẦN", row.Status);
        // Câu hỏi xin ví dụ đi vào DANH SÁCH, gắn đúng nhóm của dòng vừa bị hạ.
        var question = Assert.Single(questions);
        Assert.Equal(CoverageWorkedExampleGuard.MissingExampleQuestion, question.Text);
        Assert.Equal("Quy tắc nghiệp vụ & ràng buộc", question.Group);

        // Bằng chứng của dòng phải sống sót: nó là thứ panel tiến độ hiển thị và là chỗ distiller bám vào.
    }

    [Fact]
    public void AConfirmedWorkedExample_OpensTheRowBackUp()
    {
        var (items, questions) = Apply(RuleRowWithNumbers, "5 Responsibility với % 30/25/20/15/10 thì tổng bằng 100% → hợp lệ");

        Assert.Equal("RÕ", Row(items, "Quy tắc nghiệp vụ").Status);
        Assert.Empty(questions);
    }

    // Ranh giới 1: con số ở nhóm KHÁC không phải công thức. Số người dùng, số trường dữ liệu, số màn hình
    // không cần oracle nào — soi cả bản đồ là biến guard này thành một cái cổng đóng thường trực.
    [Fact]
    public void NumbersInOtherGroups_AreLeftAlone()
    {
        var (items, questions) = Apply("""
            - Quy mô sử dụng: [RÕ] Khoảng 1549 nhân sự toàn nhà máy dùng ứng dụng.
            - Dữ liệu / danh mục chính: [RÕ] Một JD gồm 9 thông tin: mã JD, OrgUnit, JobTitle…
            """);

        Assert.All(items, x => Assert.Equal("RÕ", x.Status));
        Assert.Empty(questions);
    }

    // Ranh giới 2: quy tắc thuần định tính (không con số) đã đủ khi nêu được điều kiện và hệ quả — không
    // có gì để tính thử, nên đòi ví dụ số ở đây là dựng một câu hỏi không có câu trả lời.
    [Fact]
    public void AQualitativeRuleRow_IsLeftAlone()
    {
        var (items, questions) = Apply("- Quy tắc nghiệp vụ & ràng buộc: [RÕ] JD phải qua HRBP verify rồi HoD approve mới available để assign.");

        Assert.Equal("RÕ", Assert.Single(items).Status);
        Assert.Empty(questions);
    }

    // Ranh giới 3: nhóm đã có câu hỏi riêng của distiller thì để nguyên — câu đó bám vào đúng quy tắc còn
    // hụt nên cụ thể hơn câu dựng sẵn, và chồng hai câu lên nhau là hỏi dồn trong một lượt.
    [Fact]
    public void AGroupThatAlreadyCarriesItsOwnQuestion_GetsNoExtraOne()
    {
        var (_, questions) = Apply("- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Responsibility có 5 cái kèm %. "
            + "còn thiếu: tổng % của các Responsibility phải bằng bao nhiêu");

        Assert.Equal("tổng % của các Responsibility phải bằng bao nhiêu", Assert.Single(questions).Text);
    }

    // ==== THU CÂU HỎI VỀ khi ví dụ đã được chốt ====
    //
    // Ca thật (dự án quản lý khóa học bắt buộc, 2026-09-05). Người dùng chốt ví dụ *"khóa hết hạn 30/6 ⇒
    // nhắc từ 1/6, mỗi tuần một email"* ở lượt 20–21 và distiller ghi đúng ví dụ ấy vào `workedExamples`,
    // nhưng vẫn chép câu xin ví dụ sang lượt sau ở trạng thái MỞ. Guard cũ `return` sớm nên không THÊM gì
    // — và cũng không dọn gì. Không lớp nào khác dọn hộ được: CoverageStaleGapGuard đo câu hỏi với cột
    // `known` của dòng, mà câu trả lời nằm ở cột WorkedExamples nên bao phủ luôn dưới ngưỡng, mãi mãi.
    // Kết quả: dòng «Quy tắc nghiệp vụ» kẹt [MỘT PHẦN] vĩnh viễn, và cổng readiness lấy nguyên câu chết ấy
    // làm câu chặn — lượt BA thật (một câu hỏi khác hẳn) bị thay trọn bằng nó trên màn hình người dùng.

    // Dòng bản đồ đúng như lượt distill của ca thật trả về: đã có ví dụ, câu xin ví dụ vẫn còn MỞ.
    private const string RuleRowStillCarryingTheGuardQuestion =
        "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Hệ thống nhắc trước 30 ngày và lặp lại hàng tuần. "
        + "còn thiếu: với quy tắc có con số ở trên, anh/chị cho mình một ví dụ cụ thể tính ra kết quả thế nào?";

    private const string ConfirmedExample =
        "Khóa học hết hạn ngày 30/6: hệ thống bắt đầu gửi email nhắc từ 1/6, mỗi tuần gửi thêm một email.";

    [Fact]
    public void AConfirmedWorkedExample_TakesBackTheQuestionTheGuardHadAsked()
    {
        var (items, questions) = Apply(RuleRowStillCarryingTheGuardQuestion, ConfirmedExample);

        Assert.Empty(questions);
        // …nhưng KHÔNG nâng trạng thái: bằng chứng do LLM chắt, không phải ô người dùng tự tay bấm. Nhóm
        // đứng [MỘT PHẦN] thì cổng rơi về nhánh PHÁT LẠI — một câu đóng lại được bằng một lượt. Quyền nâng
        // [RÕ] vẫn ở lượt distill kế tiếp.
        Assert.Equal("MỘT PHẦN", Row(items, "Quy tắc nghiệp vụ").Status);
        // Bằng chứng của dòng phải sống sót nguyên vẹn: nó là thứ nhánh PHÁT LẠI đọc ra.
        Assert.Contains("nhắc trước 30 ngày", Row(items, "Quy tắc nghiệp vụ").KnownText, StringComparison.Ordinal);
    }

    // Cái phải chứng minh là TRIỆU CHỨNG đã hết, không chỉ là danh sách đã ngắn đi một mục: câu chết ấy đi
    // lên màn hình qua cổng readiness (nó là câu chặn của cổng, không phải câu model sinh, nên phanh chống
    // hỏi lại không thấy nó).
    [Fact]
    public void OnceTakenBack_TheGateStopsAskingTheDeadQuestion()
    {
        var map = CoverageMapFixture.Map(RuleRowStillCarryingTheGuardQuestion);
        var questions = CoverageMapFixture.Questions(RuleRowStillCarryingTheGuardQuestion);

        var before = RequirementReadinessGate.Evaluate(map, questions);
        Assert.Contains("ví dụ cụ thể tính ra kết quả", before.Message, StringComparison.Ordinal);

        CoverageWorkedExampleGuard.Apply(
            CoverageMapFixture.Items(RuleRowStillCarryingTheGuardQuestion).ToList(),
            questions,
            new[] { ConfirmedExample });

        var after = RequirementReadinessGate.Evaluate(map, questions);
        Assert.DoesNotContain("ví dụ cụ thể tính ra kết quả", after.Message, StringComparison.Ordinal);
        // Cổng vẫn chưa mở — nhóm còn [MỘT PHẦN] — nhưng câu nó hỏi nay là câu người dùng trả lời được.
        Assert.False(after.Ready);
        Assert.Contains("Mình đang ghi nhận", after.Message, StringComparison.Ordinal);
    }

    // Phép thu về so bằng HẰNG SỐ nên nó chỉ chạm đúng câu của guard: câu do distiller tự viết cho một quy
    // tắc CỤ THỂ vẫn còn sống — ví dụ đã chốt không trả lời hộ nó.
    [Fact]
    public void TakingTheQuestionBack_LeavesTheDistillersOwnQuestionAlone()
    {
        var (_, questions) = Apply(
            "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Responsibility có 5 cái kèm %. "
            + "còn thiếu: với quy tắc có con số ở trên, anh/chị cho mình một ví dụ cụ thể tính ra kết quả thế nào?; "
            + "tổng % của các Responsibility phải bằng bao nhiêu",
            ConfirmedExample);

        Assert.Equal("tổng % của các Responsibility phải bằng bao nhiêu", Assert.Single(questions).Text);
    }

    // Cụm ReopenNote đứng ngoài mọi phép xoá — cùng ranh giới với CoverageStaleGapGuard. Câu đã bị đính cụm
    // ấy không còn là câu hỏi của guard mà là một lệnh MỞ LẠI nhóm, do chính người dùng phát; khớp nguyên
    // văn tự nó chừa câu này ra.
    [Fact]
    public void AReopenedQuestion_IsNeverTakenBack()
    {
        var (_, questions) = Apply(
            "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Nhắc trước 30 ngày. còn thiếu: "
            + CoverageWorkedExampleGuard.MissingExampleQuestion + " (" + AskedQuestionHistory.ReopenNote + ")",
            ConfirmedExample);

        Assert.Single(questions);
    }

    // Mục ĐÃ TRẢ LỜI ở lại danh sách: nó chở câu trả lời, đứng ngoài mọi đường hỏi sẵn rồi, và xoá đi là mời
    // lượt distill kế dựng lại nó (xem OpenQuestionEntry.Status).
    [Fact]
    public void AnAnsweredCopyOfTheQuestion_StaysInTheList()
    {
        var questions = new List<OpenQuestionEntry>
        {
            new()
            {
                Group = "Quy tắc nghiệp vụ & ràng buộc",
                Text = CoverageWorkedExampleGuard.MissingExampleQuestion,
                Status = OpenQuestionEntry.Answered,
                Answer = ConfirmedExample
            }
        };

        CoverageWorkedExampleGuard.Apply(
            CoverageMapFixture.Items("- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Nhắc trước 30 ngày.").ToList(),
            questions,
            new[] { ConfirmedExample });

        Assert.Single(questions);
    }

    // Guard chỉ HẠ, không bao giờ nâng: một dòng [KHÔNG ÁP DỤNG] hay [CHƯA HỎI] không bị đụng tới.
    [Fact]
    public void RowsThatAreNotClearOrPartial_AreUntouched()
    {
        var (items, questions) = Apply("""
            - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
            - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] người dùng nói không cần báo cáo nào.
            """);

        Assert.Equal("CHƯA HỎI", items[0].Status);
        Assert.Equal("KHÔNG ÁP DỤNG", items[1].Status);
        Assert.Empty(questions);
    }
}
