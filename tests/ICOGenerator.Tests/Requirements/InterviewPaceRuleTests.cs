using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// NHỊP CỦA BUỔI PHỎNG VẤN: đi hết một chủ đề, gộp câu đóng, BA chủ động đề xuất ý tưởng, và ngân sách lượt.
//
// Ca thật (dự án Locker management 7): 44 câu hỏi trong hơn 70 phút, mỗi lượt đúng một câu, chủ đề "tủ
// hư" bị quay lại sáu lần, BA không đề xuất ý tưởng nào — mà lại lén đưa một bước duyệt mới vào luồng qua
// bộ chip. Người dùng góp ý đúng ba điều: hỏi một chủ đề thì hỏi cho xong; BA nên đề xuất nhiều hơn là
// chỉ hỏi; và cả buổi nên gói trong một giờ.
//
// Không phép thử tất định nào chấm được "câu này có thuộc chủ đề đang bàn dở không" — đó là luật phỏng
// vấn, nên tầng chặn là prompt + golden set, và các test dưới đây giữ cho chúng không âm thầm rơi mất.
// Phần duy nhất máy làm được (đếm lượt) nằm ở InterviewPaceBudget và có test riêng.
public class InterviewPaceRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void ChatPrompt_FinishesATopicBeforeMovingOn()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("## ĐI HẾT MỘT CHỦ ĐỀ rồi mới sang chủ đề khác", prompt, StringComparison.Ordinal);
        // Bản đồ là danh sách kiểm, không phải thứ tự hỏi — câu này là lõi của cả mục.
        Assert.Contains("nó KHÔNG phải thứ tự hỏi", prompt, StringComparison.Ordinal);
        Assert.Contains("không quay lại chủ đề đã rời", prompt, StringComparison.Ordinal);
        // Quay lại (khi buộc phải) thì gom hết vào một lượt.
        Assert.Contains("gom MỌI điều còn hụt của chủ đề đó vào đúng một lượt", prompt, StringComparison.Ordinal);
        // Bước chọn câu hỏi kế tiếp phải trỏ tới mục này, nếu không thứ tự nhóm của bản đồ vẫn thắng.
        Assert.Contains("hỏi cho HẾT chủ đề đó trước", prompt, StringComparison.Ordinal);
        // Ca thật dạy model nhận ra chính mình đang nhảy chủ đề.
        Assert.Contains("hỏi xong để đó rồi một lúc sau mới hỏi lại", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPrompt_BatchesIndependentClosedQuestions_ByDefault()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("đang cầm từ 2 câu ĐÓNG độc lập trở lên thì GỘP 2–4 câu vào một lượt", prompt, StringComparison.Ordinal);
        // Trần 4 câu và danh sách "bắt buộc hỏi một mình" là hai thứ giữ cho việc gộp không thành hỏi dồn.
        Assert.Contains("Trần cứng: tối đa 4 câu một lượt", prompt, StringComparison.Ordinal);
        Assert.Contains("BẮT BUỘC hỏi MỘT MÌNH", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPrompt_MakesTheBAProposeIdeas_ThroughOneVisibleDoor()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("## ĐỀ XUẤT Ý TƯỞNG", prompt, StringComparison.Ordinal);
        Assert.Contains("bạn PHẢI chủ động nêu những ý tưởng người dùng chưa nghĩ tới", prompt, StringComparison.Ordinal);
        // Mỗi ý một căn cứ, và người dùng tích thì mới vào phạm vi.
        Assert.Contains("Mỗi ý tưởng phải chỉ ra được MỘT căn cứ đã có", prompt, StringComparison.Ordinal);
        Assert.Contains("Ý không được tích thì bỏ hẳn", prompt, StringComparison.Ordinal);
        // Có trần: đề xuất tràn lan là phá chính mục tiêu một giờ.
        Assert.Contains("tối đa HAI lượt đề xuất cả buổi", prompt, StringComparison.Ordinal);
        // Ranh giới nền tảng áp cho ý tưởng như cho câu hỏi.
        Assert.Contains("Nằm trong ranh giới đã chốt", prompt, StringComparison.Ordinal);
        // Và ý tưởng không được đi cửa sau qua chip của một câu hỏi thường.
        Assert.Contains("Ý tưởng KHÔNG được đi cửa sau qua bộ chip", prompt, StringComparison.Ordinal);
        // Luật cũ "đừng bịa điểm đau" không bị nới: file gợi ra ý tưởng, không gợi ra điểm đau.
        Assert.Contains("không mở thêm điểm đau mới từ file", prompt, StringComparison.Ordinal);
    }

    // Prompt nền chỉ giữ CON TRỎ tới khối chặng về đích; chữ của khối nằm ở code vì nó là một nhánh trạng
    // thái do cơ chế chọn. Con trỏ lệch tên heading, hoặc hai bên nói hai con số khác nhau, là model nhận
    // một lệnh nó không tìm thấy.
    [Fact]
    public void ChatPrompt_PointsAtTheClosingBlock_AndAgreesWithTheBudgetInCode()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("## Ngân sách buổi phỏng vấn", prompt, StringComparison.Ordinal);
        Assert.Contains("\"" + BAChatPromptBlocks.InterviewClosingHeading + "\"", prompt, StringComparison.Ordinal);
        Assert.Contains($"khoảng {InterviewPaceBudget.ClosingAfterUserTurns} lượt", prompt, StringComparison.Ordinal);
        // Chữ của khối KHÔNG được chép vào prompt nền — nó vào mọi lượt nên sẽ giục đóng nhóm từ lượt đầu.
        Assert.DoesNotContain("Người dùng chốt TỪNG câu", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CoveragePrompt_RecordsAnIdeaOnlyWhenTheUserPickedIt()
    {
        var prompt = CoveragePromptFixture.Read();

        Assert.Contains("(BA đề xuất, người dùng đồng ý)", prompt, StringComparison.Ordinal);
        Assert.Contains("không ghi ở đâu cả", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GoldenSet_ScoresTopicOrder_Batching_AndProposals()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c => c.Contains("ĐI HẾT MỘT CHỦ ĐỀ", StringComparison.Ordinal));
        Assert.Contains(criteria, c => c.Contains("GỘP 2–4 câu một lượt", StringComparison.Ordinal));
        Assert.Contains(criteria, c => c.Contains("ĐỀ XUẤT ít nhất một ý tưởng", StringComparison.Ordinal));
        // Tiêu chí cũ chấm ngược với prompt: phạt đúng lượt gộp mà prompt bắt phải gộp.
        Assert.DoesNotContain(criteria, c => c.Contains("Mỗi lượt chỉ hỏi MỘT câu", StringComparison.Ordinal));
    }

    // Cùng cách tìm Prompts/ như BAChatPlaybackRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
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
