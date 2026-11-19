using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MỐC THỜI GIAN CỦA CÂU HỎI: HIỆN NAY hay Ở ỨNG DỤNG MỚI.
//
// Ca thật (dự án quản lý locker, call log BAChat 2026-11-19): người dùng không phân biệt được BA đang hỏi
// cách làm hiện nay hay cách ứng dụng mới nên làm. Lượt BA hỏi "kể giúp mình lần gần nhất xử lý … Mỗi
// bước ai làm, và tủ được đánh dấu hay ghi lại như thế nào?" — vế đầu là chuyện đã xảy ra, vế sau đọc
// như hỏi thiết kế. Khối «Điểm cần làm rõ» còn ghi "khi một nhân viên mới cần tủ, ai nhận yêu cầu, ai
// chọn và giao tủ, và việc đó được ghi lại ở đâu" (không mốc nào) và "một tủ đi qua những trạng thái
// nào (ví dụ đang dùng, chờ thu hồi, chờ kiểm tra…)" — file chỉ có 4 trạng thái, phần ví dụ là trạng
// thái BA tự nghĩ chen vào câu hỏi hiện trạng. Hồ sơ người dùng cũng đã ghi "khi gợi ý của BA không khớp
// thực tế, tự mô tả lại quy trình thật": đề xuất cho ứng dụng mới bị đọc thành câu hỏi về hiện trạng.
//
// Hậu quả thứ hai, khó thấy hơn: bản đồ bao phủ ghi cách làm cũ (HR gửi file Excel, 4 trạng thái theo
// màu ô) vào «Luồng nghiệp vụ chính» và «Vòng đời & trạng thái» như yêu cầu, và Product Brief chép
// nguyên cơ chế của công cụ cũ thành chức năng. Vì vậy luật đi qua cả dây chuyền: prompt chat (câu hỏi
// mang mốc), prompt bản đồ (`text` mang mốc + nhãn "Hiện nay:" trên `known`), hai trọng tài (mẩu
// "Hiện nay:" không trả lời câu hỏi về ứng dụng mới; khác mốc không phải hỏi lại), Product Brief và vòng
// tự soát (cách làm cũ không phải chức năng).
//
// Không có chốt chặn tất định nào ở đây — đọc nghĩa của một câu tiếng Việt là việc của model. Test chỉ
// giữ cho các luật không âm thầm rơi khỏi prompt.
public class BAChatTimeFrameRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string Tag = "Hiện nay: ";

    [Fact]
    public void ChatPrompt_RequiresATimeFrameOnEveryProcessQuestion()
    {
        var prompt = PromptFixture.Read(ChatPromptKey);

        Assert.Contains("## MỐC THỜI GIAN của câu hỏi: HIỆN NAY hay Ở ỨNG DỤNG MỚI", prompt, StringComparison.Ordinal);
        Assert.Contains("*\"Hiện nay, …\"*", prompt, StringComparison.Ordinal);
        Assert.Contains("*\"Ở ứng dụng mới, …\"*", prompt, StringComparison.Ordinal);
        Assert.Contains("Một câu, một mốc.", prompt, StringComparison.Ordinal);
        // Trạng thái/bước BA tự nghĩ ra là đề xuất, không chen vào câu hỏi hiện trạng.
        Assert.Contains("Ví dụ trong ngoặc của một câu \"Hiện nay\" chỉ lấy từ điều đã có", prompt, StringComparison.Ordinal);
        // BA phải biết nhãn của bản đồ nghĩa là gì, nếu không nó đọc cách làm cũ thành yêu cầu đã chốt.
        Assert.Contains("*\"Hiện nay:\"*", prompt, StringComparison.Ordinal);
        // Và soát lần cuối nhắc lại đúng điều dễ trượt này.
        Assert.Contains("mở bằng mốc *\"Hiện nay, …\"* hoặc *\"Ở ứng dụng mới, …\"*", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CoveragePrompt_TagsTheOldWayOfWorking_AndKeepsNewAppQuestionsOpen()
    {
        var prompt = PromptFixture.Read(CoveragePromptKey);

        Assert.Contains("phải mở đầu bằng đúng cụm `" + Tag + "`", prompt, StringComparison.Ordinal);
        // Nhãn được gỡ khi người dùng chốt giữ nguyên/đổi — kể cả câu "cứ làm y như hiện tại".
        Assert.Contains("câu này áp cho mọi phần tử `Hiện nay:` của buổi", prompt, StringComparison.Ordinal);
        // Nhãn KHÔNG phải câu hỏi treo: không có luật này thì mỗi phần tử đẻ ra một câu "có giữ không" và
        // dòng kẹt [MỘT PHẦN] — đúng hình dạng vòng lặp câu hỏi chết.
        Assert.Contains("Nhãn không phải một câu hỏi còn treo", prompt, StringComparison.Ordinal);
        Assert.Contains("**`text` về quy trình phải nói rõ nó hỏi HIỆN NAY hay Ở ỨNG DỤNG MỚI.**", prompt, StringComparison.Ordinal);
        Assert.Contains("**không đóng** một câu hỏi *\"ở ứng dụng mới …\"*", prompt, StringComparison.Ordinal);
        // Cụm tín hiệu máy của mục đính chính vẫn phải đứng đầu `text`.
        Assert.Contains("mốc thời gian đứng ngay sau cụm đó", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Judges_DoNotTreatTheOldWayAsAnAnswerOrADifferentTimeFrameAsARepeat()
    {
        var questionJudge = PromptFixture.Read("BusinessAnalyst/coverage-question-judge.v1.md");
        Assert.Contains("**`Hiện nay:`**", questionJudge, StringComparison.Ordinal);
        Assert.Contains("**không** trả lời được câu hỏi về ứng dụng mới", questionJudge, StringComparison.Ordinal);

        var turnJudge = PromptFixture.Read("BusinessAnalyst/turn-judge.v1.md");
        Assert.Contains("**Cùng chủ đề nhưng khác MỐC THỜI GIAN**", turnJudge, StringComparison.Ordinal);
    }

    [Fact]
    public void BriefPrompts_DoNotTurnTheOldToolIntoAFeature()
    {
        var brief = PromptFixture.Read("BusinessAnalyst/product-brief.v3.md");
        Assert.Contains("**CÁCH LÀM CŨ KHÔNG PHẢI CHỨC NĂNG CỦA ỨNG DỤNG:**", brief, StringComparison.Ordinal);
        Assert.Contains("mở bằng `Hiện nay:`", brief, StringComparison.Ordinal);
        Assert.Contains("**Bỏ cơ chế của công cụ cũ**", brief, StringComparison.Ordinal);

        var review = PromptFixture.Read("BusinessAnalyst/product-brief-review.v2.md");
        Assert.Contains("10. **Cách làm cũ viết thành chức năng của ứng dụng**", review, StringComparison.Ordinal);
    }

    [Fact]
    public void GoldenSet_ScoresTheTimeFrameOfQuestions_AndTheTagOnTheMap()
    {
        var scenarios = EvalScenariosSeedData.Build();

        Assert.Contains(scenarios, s =>
            s.PromptKey == ChatPromptKey
            && s.Criteria.Contains("MỞ ĐẦU bằng một mốc thời gian", StringComparison.Ordinal));
        Assert.Contains(scenarios, s =>
            s.PromptKey == CoveragePromptKey
            && s.Criteria.Contains("MỞ ĐẦU bằng \"" + Tag + "\"", StringComparison.Ordinal));
    }
}
