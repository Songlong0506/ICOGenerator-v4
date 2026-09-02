using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bước soạn Product Brief đọc TRANSCRIPT THÔ, và transcript thô là chỗ yêu cầu đi lạc: một quyết định
// người dùng chốt ở giữa buổi rồi không ai nhắc lại tới cuối vẫn là yêu cầu phải có trong tài liệu, nhưng
// trong 70 lượt chat nó chìm. Vòng tự soát cũng không cứu được vì reviewer cũng chỉ có đúng transcript ấy
// để đối chiếu — hai lượt LLM cùng đọc một đống chữ và cùng bỏ sót một chỗ.
//
// Ca thật đã gặp: người dùng chốt nhân viên ĐƯỢC HỦY ĐĂNG KÝ (lượt 38–39), bản nháp bỏ hẳn tính năng đó
// nhưng vẫn giữ hai quy tắc dựa vào nó ("Admin chỉ từ chối ticket waitlist khi nhân viên đã hủy đăng ký")
// — tài liệu tự nói tới một hành động mà chính nó không cho ai làm, và người duyệt đọc lướt sẽ gật.
//
// Chốt chặn: đưa các danh sách MÁY ĐÃ CHẮT (Ví dụ đã xác nhận / Điểm còn tồn đọng) vào cả
// lượt soạn, lượt soát và lượt sửa, để "đừng bỏ sót yêu cầu nào" chuyển từ một lời dặn thành một phép
// đối chiếu đếm được. Các test dưới giữ cho mối nối đó (code dựng khối + prompt bảo đối chiếu) không rơi.
public class BriefTraceabilityRuleTests
{
    private const string ComposePromptKey = "BusinessAnalyst/product-brief.v3.md";
    private const string ReviewPromptKey = "BusinessAnalyst/product-brief-review.v2.md";

    private static readonly Project SampleProject = new()
    {
        Name = "Kế hoạch đào tạo",
        Description = "app lập kế hoạch lớp học",
        WorkedExamples = InterviewOutlookParser.SerializeWorkedExamples(new[] { "Tính số lớp: nhu cầu 25, tối đa 10/lớp → 3 lớp." }),
        OpenQuestions = OpenQuestionFixture.Stored("[Dữ liệu / danh mục chính] Chưa rõ ai quản lý danh mục khóa học.")
    };

    [Fact]
    public void ComposePrompt_CarriesTheDistilledStateIntoAllThreeCalls()
    {
        var builder = new RequirementPromptBuilder();
        const string distilled = "### Ví dụ đã xác nhận\n- Nhân viên có thể hủy đăng ký lớp đã enroll.";

        var compose = builder.BuildProductBrief(SampleProject, "hội thoại", "", "", distilled);
        var review = builder.BuildProductBriefReview(SampleProject, "hội thoại", "bản nháp", "", distilled);
        var revision = builder.BuildProductBriefRevision(SampleProject, "hội thoại", "bản nháp", new[] { "vấn đề" }, "", distilled);

        // Cả ba lượt phải thấy cùng một danh sách. Thiếu ở lượt SOÁT là hỏng nặng nhất: đó là cổng cuối
        // cùng trước khi bản nháp tới tay người duyệt.
        foreach (var prompt in new[] { compose, review, revision })
        {
            Assert.Contains("Nhân viên có thể hủy đăng ký", prompt, StringComparison.Ordinal);
            Assert.Contains("đối chiếu MÁY MÓC", prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Dự án chưa chắt được gì (buổi phỏng vấn quá ngắn, hoặc lượt chắt lỗi) ⇒ prompt phải trở về đúng
    // hình dạng cũ, không để lại một khối tiêu đề rỗng khiến model tưởng danh sách rỗng nghĩa là
    // "người dùng không chốt điều gì cả".
    [Fact]
    public void ComposePrompt_OmitsTheSectionEntirely_WhenNothingWasDistilled()
    {
        var builder = new RequirementPromptBuilder();

        var prompt = builder.BuildProductBrief(SampleProject, "hội thoại", "", "", "");

        Assert.DoesNotContain("Trạng thái đã chắt", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePrompt_TellsTheWriterToUseTheDistilledStateAsAChecklist()
    {
        var prompt = ReadPrompt(ComposePromptKey);

        Assert.Contains("Trạng thái đã chắt từ hội thoại", prompt, StringComparison.Ordinal);
        // Điểm còn treo KHÔNG được biến thành một phương án viết ra như điều đã chốt — đó chính là van
        // needsClarification, thứ duy nhất còn lại sau khi cổng readiness đã cho qua.
        Assert.Contains("needsClarification", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewPrompt_MakesTheCrossCheckTheFirstThingItDoes()
    {
        var prompt = ReadPrompt(ReviewPromptKey);

        Assert.Contains("Đối chiếu MÁY MÓC", prompt, StringComparison.Ordinal);
        Assert.Contains("Ví dụ đã xác nhận", prompt, StringComparison.Ordinal);
        Assert.Contains("Điểm cần làm rõ còn tồn đọng", prompt, StringComparison.Ordinal);
    }

    // Ba loại lỗi mà reviewer cũ KHÔNG soi vì nó chỉ so tài liệu với hội thoại, trong khi cả ba nhìn thấy
    // được ngay bên trong chính bản nháp: hai câu nói ngược nhau, một tính năng không có màn hình để làm,
    // một trường bắt nhập mà không luật nào dùng.
    [Fact]
    public void ReviewPrompt_AlsoCatchesDefectsVisibleInsideTheDraftItself()
    {
        var prompt = ReadPrompt(ReviewPromptKey);

        Assert.Contains("Tài liệu tự mâu thuẫn", prompt, StringComparison.Ordinal);
        Assert.Contains("không có chỗ thực hiện", prompt, StringComparison.Ordinal);
        Assert.Contains("Dữ liệu mồ côi", prompt, StringComparison.Ordinal);
    }

    // Cùng cách đọc prompt như các rule test khác: ưu tiên bản copy cạnh test binary, rồi lần ngược cây
    // thư mục về Prompts/ của repo.
    private static string ReadPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return File.ReadAllText(fromBin);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
    }
}
